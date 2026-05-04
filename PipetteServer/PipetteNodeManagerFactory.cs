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
    public class PipetteNodeManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new PipetteNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => ["http://lab.server/Pipette/"];
    }

    public class PipetteNodeManager : CustomNodeManager2
    {
        private CancellationTokenSource _pipettingCts;

        public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Pipette/")
        {
            SystemContext.NodeIdFactory = this; 
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Pipette/"
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

                var pipetteAssembly = typeof(PipetteNodeManager).Assembly;
                ImportXmlResource(externalReferences, pipetteAssembly, "PipetteServer.Pipette.xml");

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
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Pipette/");

            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState startMethod)
                startMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState stopMethod)
                stopMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7024u, ns), typeof(MethodState)) is MethodState dcStopMethod)
                dcStopMethod.OnCallMethod = Method_OnCall;

            uint[] writableVariableIds = [ 6018u, 6201u, 6203u ];

            foreach (var varId in writableVariableIds)
            {
                if (FindPredefinedNode(new NodeId(varId, ns), typeof(BaseVariableState)) is BaseVariableState variableNode)
                {
                    variableNode.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
                }
            }
            
            // Инициализация статуса
            if (FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) is BaseVariableState stateNode)
                UpdateNodeValue(stateNode, new Opc.Ua.LocalizedText("en", "Idle"));
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Pipette Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good; 
        }

        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Pipette Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartPipetting")
            {
                StartPipettingTask();
            }
            else if (method.BrowseName.Name == "StopPipetting" || method.BrowseName.Name == "Stop")
            {
                _pipettingCts?.Cancel();
                Console.WriteLine("[Pipette]: Pipetting manually aborted.");
            }

            return StatusCodes.Good;
        }

        private void StartPipettingTask()
        {
            _pipettingCts?.Cancel();
            _pipettingCts = new CancellationTokenSource();
            var token = _pipettingCts.Token;

            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Pipette/");
            
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;    
            var currentVolumeNode = FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) as BaseVariableState;   
            var totalizedVolumeNode = FindPredefinedNode(new NodeId(6202u, ns), typeof(BaseVariableState)) as BaseVariableState; 

            Task.Run(async () =>
            {
                try
                {
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                    double currentVolume = 0;

                    double totalVolume = 0;
                    if (totalizedVolumeNode?.Value is double existingTotal) totalVolume = existingTotal; 

                    for (int i = 0; i < 5; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        currentVolume += 10.5; 
                        totalVolume += 10.5;
                        
                        UpdateNodeValue(currentVolumeNode, currentVolume);
                        UpdateNodeValue(totalizedVolumeNode, totalVolume);

                        Console.WriteLine($"[Pipette]: Aspirating process... Volume in tip: {currentVolume} uL");
                        await Task.Delay(1000, token);
                    }

                    // Успешное завершение
                    Console.WriteLine("[Pipette]: Dispensing everything...");
                    await Task.Delay(1000, token);
                    token.ThrowIfCancellationRequested();

                    UpdateNodeValue(currentVolumeNode, 0.0); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                    
                    Console.WriteLine("[Pipette]: Pipetting Completed successfully.");
                    Console.WriteLine("StateMachineStatus: Completed - Return to Idle");

                    // Возврат в Idle
                    await Task.Delay(1000); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                    Console.WriteLine("StateMachineStatus: Idle");
                }
                catch (OperationCanceledException)
                {
                    UpdateNodeValue(currentVolumeNode, 0.0);
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                    
                    Console.WriteLine("[Pipette]: Pipetting manually aborted. State -> Aborted");

                    // Возврат в Idle
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
            }
        }
    }
}