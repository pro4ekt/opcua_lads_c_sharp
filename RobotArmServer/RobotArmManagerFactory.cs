using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace OpcUa.Lads.Foundation.Server
{
    public class RobotArmManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new RobotArmNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => ["http://lab.server/RobotArmServer/"];
    }

    public class RobotArmNodeManager : CustomNodeManager2
    {
        private CancellationTokenSource _robotCts;

        public RobotArmNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/RobotArmServer/")
        {
            SystemContext.NodeIdFactory = this;
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/RobotArmServer/"
            ];
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                var foundationAssembly = typeof(OpcUa.Lads.Foundation.Server.NodeManager).Assembly;

                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.AMB.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
                ImportXmlResource(externalReferences, foundationAssembly, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

                var robotAssembly = typeof(RobotArmNodeManager).Assembly;
                ImportXmlResource(externalReferences, robotAssembly, "RobotArmServer.RobotArm.xml");

                AddReverseReferences(externalReferences);
                AttachLogicHandlers();
            }
        }

        private void ImportXmlResource(IDictionary<NodeId, IList<IReference>> externalReferences, System.Reflection.Assembly assembly, string resourcePath)
        {
            using var stream = assembly.GetManifestResourceStream(resourcePath);
            if (stream == null) 
            {
                throw new Exception($"Cannot find embedded resource: {resourcePath} in assembly {assembly.FullName}");
            }
            var nodeSet = UANodeSet.Read(stream);
            foreach (var nameSpace in nodeSet.NamespaceUris)
            {
                SystemContext.NamespaceUris.GetIndexOrAppend(nameSpace);
            }

            var predefinedNodes = new NodeStateCollection();
            nodeSet.Import(SystemContext, predefinedNodes);
            
            var toImportNodes = new List<NodeState>();
            foreach (var node in predefinedNodes)
            {
                if (node is BaseTypeState state && state.SuperTypeId != null &&
                    node.NodeId.NamespaceIndex == state.SuperTypeId.NamespaceIndex &&
                    !PredefinedNodes.ContainsKey(state.SuperTypeId))
                {
                    toImportNodes.Add(node);
                }
                else
                {
                    AddPredefinedNode(SystemContext, node);
                }
            }

            foreach (var node in toImportNodes)
            {
                AddPredefinedNode(SystemContext, node);
            }
        }

        private void AttachLogicHandlers()
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/RobotArmServer/");

            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState moveToAMethod)
                moveToAMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState moveToCentrifugeMethod)
                moveToCentrifugeMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7019u, ns), typeof(MethodState)) is MethodState moveToBMethod)
                moveToBMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7020u, ns), typeof(MethodState)) is MethodState moveStopMethod)
                moveStopMethod.OnCallMethod = Method_OnCall;

            uint[] writableVariableIds = [ 6018u, 6201u, 6203u ]; 
            foreach (var varId in writableVariableIds)
            {
                if (FindPredefinedNode(new NodeId(varId, ns), typeof(BaseVariableState)) is BaseVariableState varNode)
                {
                    varNode.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
                }
            }
            
            // Зададим начальное положение робота (0.0 = Home)
            if (FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) is BaseVariableState curLoc)
                UpdateNodeValue(curLoc, 0.0);
                
            if (FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) is BaseVariableState stateNode)
                UpdateNodeValue(stateNode, new Opc.Ua.LocalizedText("en", "Idle"));
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[RobotArmServer]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good;
        }

        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/RobotArmServer/");
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;
            string stateBefore = (currentStateNode?.Value as Opc.Ua.LocalizedText)?.Text ?? "Unknown";

            Console.WriteLine($"[RobotArmServer]: Execute Command => '{method.BrowseName.Name}'. State Before: {stateBefore}");

            // Mapping: 1.0 = Point A, 2.0 = Point B, 3.0 = Centrifuge
            if (method.BrowseName.Name == "MoveToA")
                StartMoveTask(1.0, "Point A");
            else if (method.BrowseName.Name == "MoveToCentrifuge")
                StartMoveTask(3.0, "Centrifuge");
            else if (method.BrowseName.Name == "MoveToB")
                StartMoveTask(2.0, "Point B");
            else if (method.BrowseName.Name == "MoveStop")
            {
                _robotCts?.Cancel();
                Console.WriteLine("[RobotArmServer]: Movement manually stopped.");
            }

            string stateAfter = (currentStateNode?.Value as Opc.Ua.LocalizedText)?.Text ?? "Unknown";
            Console.WriteLine($"[RobotArmServer]: Execute Command => '{method.BrowseName.Name}' dispatched. State After (immediate): {stateAfter}");

            return StatusCodes.Good;
        }

                private void StartMoveTask(double destinationValue, string destinationName)
        {
            _robotCts?.Cancel();
            _robotCts = new CancellationTokenSource();
            var token = _robotCts.Token;

            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/RobotArmServer/");
            
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;
            var currentLocationNode = FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) as BaseVariableState;
            var targetLocationNode = FindPredefinedNode(new NodeId(6201u, ns), typeof(BaseVariableState)) as BaseVariableState;

            Task.Run(async () =>
            {
                try
                {
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Moving"));
                    UpdateNodeValue(targetLocationNode, destinationValue);

                    Console.WriteLine($"[RobotArmServer]: Starting movement to {destinationName} (Point {destinationValue})...");
                    
                    // Симуляция 3 секунд перемещения
                    await Task.Delay(3000, token); 
                    
                    token.ThrowIfCancellationRequested();

                    // Доехали
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                    UpdateNodeValue(currentLocationNode, destinationValue);
                    Console.WriteLine($"[RobotArmServer]: Successfully arrived at {destinationName}.");
                    
                    // Вывод о статусе Complete
                    Console.WriteLine("StateMachineStatus: Completed - Return to Idle");

                    // Возврат в Idle для готовности к новой команде
                    await Task.Delay(1000); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                    
                    // Вывод о переходе в статус Idle
                    Console.WriteLine("StateMachineStatus: Idle");
                }
                catch (OperationCanceledException)
                {
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                    Console.WriteLine($"[RobotArmServer]: Movement to {destinationName} was cancelled.");

                    // Возврат в Idle после отмены
                    await Task.Delay(1000); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                    Console.WriteLine("StateMachineStatus: Idle");
                }
            }, token);
        }
        private void UpdateNodeValue(BaseVariableState node, object newValue)
        {
            if (node != null)
            {
                node.Value = newValue;
                node.Timestamp = DateTime.UtcNow;
                node.StatusCode = StatusCodes.Good;
                // Удалено: node.ClearChangeMasks(SystemContext, false); 
            }
        }
    }
}