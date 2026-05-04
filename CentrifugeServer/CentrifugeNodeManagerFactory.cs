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
    public class CentrifugeNodeManagerFactory : INodeManagerFactory
    {
        public INodeManager Create(IServerInternal server, ApplicationConfiguration configuration)
        {
            return new CentrifugeNodeManager(server, configuration);
        }

        public StringCollection NamespacesUris => ["http://lab.server/Centrifuge/"];
    }

    public class CentrifugeNodeManager : CustomNodeManager2
    {
        public Action OnStartProgramCalled { get; set; }
        
        private CancellationTokenSource _spinningCts;
        
        public CentrifugeNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/")
        {
            SystemContext.NodeIdFactory = this; 
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/"
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

                var centrifugeAssembly = typeof(CentrifugeNodeManager).Assembly;
                ImportXmlResource(externalReferences, centrifugeAssembly, "CentrifugeServer.Centrifuge.xml");

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

            AddReverseReferences(externalReferences);
        }
        
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good; 
        }
        
        private void AttachLogicHandlers()
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");

            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState startSpinningMethod)
                startSpinningMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState stopSpinningMethod)
                stopSpinningMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7024u, ns), typeof(MethodState)) is MethodState stopSpeedMethod)
                stopSpeedMethod.OnCallMethod = Method_OnCall;

            if (FindPredefinedNode(new NodeId(7025u, ns), typeof(MethodState)) is MethodState stopTempMethod)
                stopTempMethod.OnCallMethod = Method_OnCall;

            uint[] writableVariableIds = 
            [
                6018u, // AssetId
                6201u, // TargetValue
                6203u, // Speed
                6301u, // Temperature Target
                6303u  // Temperature
            ];

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
        
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartSpinning")
            {
                OnStartProgramCalled?.Invoke();
                StartSpinningTask();
            }
            else if (method.BrowseName.Name == "StopSpinning" || method.BrowseName.Name == "Stop")
            {
                _spinningCts?.Cancel();
                Console.WriteLine("[Centrifuge]: Spinning manually aborted.");
            }

            return StatusCodes.Good;
        }

        private void StartSpinningTask()
        {
            _spinningCts?.Cancel();
            _spinningCts = new CancellationTokenSource();
            var token = _spinningCts.Token;
            
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");
            
            var currentStateNode = FindPredefinedNode(new NodeId(6197u, ns), typeof(BaseVariableState)) as BaseVariableState;
            var speedCurrentValueNode = FindPredefinedNode(new NodeId(6200u, ns), typeof(BaseVariableState)) as BaseVariableState;

            Task.Run(async () =>
            {
                try
                {
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Running"));
                    double currentSpeed = 0;

                    for (int i = 0; i < 5; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        currentSpeed += 500; 
                        UpdateNodeValue(speedCurrentValueNode, currentSpeed);

                        Console.WriteLine($"[Centrifuge]: Spinning... Speed: {currentSpeed} RPM");
                        await Task.Delay(1000, token);
                    }

                    // Успешное завершение
                    UpdateNodeValue(speedCurrentValueNode, 0.0);
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Complete"));
                    
                    Console.WriteLine("[Centrifuge]: Spinning Completed Successfully.");
                    Console.WriteLine("StateMachineStatus: Completed - Return to Idle");

                    // Возврат в Idle
                    await Task.Delay(1000); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Idle"));
                    Console.WriteLine("StateMachineStatus: Idle");
                }
                catch (OperationCanceledException)
                {
                    // Отмена
                    UpdateNodeValue(speedCurrentValueNode, 0.0); 
                    UpdateNodeValue(currentStateNode, new Opc.Ua.LocalizedText("en", "Aborted"));
                    
                    Console.WriteLine("[Centrifuge]: Spinning manually aborted. State -> Aborted");

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