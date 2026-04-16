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

        public StringCollection NamespacesUris => ["http://lab.server/RobotArm/"];
    }

    public class RobotArmNodeManager : CustomNodeManager2
    {
        private BaseDataVariableState _moveSpeedVar;

        public RobotArmNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, "http://lab.server/RobotArm/")
        {
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                string nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "NodeSet", "RobotArm.xml");
                if (!File.Exists(nodeSetFilePath))
                {
                    nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "NodeSet", "RobotArm.xml");
                }

                NodeStateCollection predefinedNodes = new NodeStateCollection();
                using (Stream stream = File.OpenRead(nodeSetFilePath))
                {
                    // UANodeSet.Read выполняет физическое чтение XML.
                    UANodeSet nodeSet = UANodeSet.Read(stream);
                    
                    // Добавляем пространства имен из XML в контекст сервера
                    foreach (var nameSpace in nodeSet.NamespaceUris)
                    {
                        SystemContext.NamespaceUris.GetIndexOrAppend(nameSpace);
                    }

                    // Import "разворачивает" XML теги в объекты C# (NodeState) и помещает их в коллекцию
                    nodeSet.Import(SystemContext, predefinedNodes);
                }

                // 2. Добавляем загруженные узлы в наше адресное пространство (ПУБЛИКАЦИЯ В ПАМЯТИ)
                for (int i = 0; i < predefinedNodes.Count; i++)
                {
                    AddPredefinedNode(SystemContext, predefinedNodes[i]);
                }

                ushort ns = NamespaceIndexes[0];
                var rootNodeId = new NodeId("RobotArm", ns);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, rootNodeId));

                AddReverseReferences(externalReferences);
                AttachLogicHandlers();
            }
        }

        private void AttachLogicHandlers()
        {
            ushort ns = NamespaceIndexes[0];

            _moveSpeedVar = FindPredefinedNode(new NodeId("RobotArm_Functions_MoveSpeed", ns), typeof(BaseDataVariableState)) as BaseDataVariableState;

            if (FindPredefinedNode(new NodeId("RobotArm_Functions_Grab", ns), typeof(MethodState)) is MethodState grabMethod)
            {
                grabMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId("RobotArm_Functions_Move", ns), typeof(MethodState)) is MethodState moveMethod)
            {
                moveMethod.OnCallMethod = Method_OnCall;
            }

            if (FindPredefinedNode(new NodeId("RobotArm_Functions_Release", ns), typeof(MethodState)) is MethodState releaseMethod)
            {
                releaseMethod.OnCallMethod = Method_OnCall;
            }

            if (_moveSpeedVar != null)
            {
                _moveSpeedVar.OnWriteValue = OnVariableWrite;
            }
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[RobotArm Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good;
        }

        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[RobotArm Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "Grab")
            {
                Console.WriteLine("[RobotArm]: Grabbing object...");
            }
            else if (method.BrowseName.Name == "Move")
            {
                Console.WriteLine("[RobotArm]: Moving object...");
            }
            else if (method.BrowseName.Name == "Release")
            {
                Console.WriteLine("[RobotArm]: Releasing object...");
            }

            return StatusCodes.Good;
        }
    }
}