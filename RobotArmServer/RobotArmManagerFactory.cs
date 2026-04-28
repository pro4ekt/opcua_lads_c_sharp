using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;

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
            {
                moveToAMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId(7018u, ns), typeof(MethodState)) is MethodState moveToCentrifugeMethod)
            {
                moveToCentrifugeMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId(7019u, ns), typeof(MethodState)) is MethodState moveToBMethod)
            {
                moveToBMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId(7020u, ns), typeof(MethodState)) is MethodState moveStopMethod)
            {
                moveStopMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId(6018u, ns), typeof(BaseVariableState)) is BaseVariableState assetIdVar)
            {
                assetIdVar.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
            }
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[RobotArmServer Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good;
        }

        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[RobotArmServer Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "MoveToA")
            {
                StartMoveTask("Point A");
            }
            else if (method.BrowseName.Name == "MoveToCentrifuge")
            {
                StartMoveTask("Centrifuge");
            }
            else if (method.BrowseName.Name == "MoveToB")
            {
                StartMoveTask("Point B");
            }
            else if (method.BrowseName.Name == "MoveStop")
            {
                _robotCts?.Cancel();
                Console.WriteLine("[RobotArmServer]: Movement manually stopped.");
            }

            return StatusCodes.Good;
        }

        private void StartMoveTask(string destination)
        {
            _robotCts?.Cancel();
            _robotCts = new CancellationTokenSource();
            var token = _robotCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[RobotArmServer]: Starting movement to {destination}...");
                    await Task.Delay(3000, token); // Симуляция 3 секунд перемещения
                    
                    if (!token.IsCancellationRequested)
                    {
                        Console.WriteLine($"[RobotArmServer]: Successfully arrived at {destination}.");
                    }
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine($"[RobotArmServer]: Movement to {destination} was cancelled.");
                }
            }, token);
        }
    }
}