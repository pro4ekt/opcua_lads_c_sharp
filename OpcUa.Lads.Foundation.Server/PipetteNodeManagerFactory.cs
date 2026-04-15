using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;

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
        public PipetteNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, "http://lab.server/Pipette/")
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

                // 1. Читаем структуру из XML файла
                string nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "NodeSet", "Pipette.xml");
                if (!File.Exists(nodeSetFilePath))
                {
                    // Попытка найти рядом с exe, если запущено не из студии
                    nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "NodeSet", "Pipette.xml");
                }

                NodeStateCollection predefinedNodes = new NodeStateCollection();
                using (Stream stream = File.OpenRead(nodeSetFilePath))
                {
                    UANodeSet nodeSet = UANodeSet.Read(stream);
                    nodeSet.Import(SystemContext, predefinedNodes);
                }

                // 2. Добавляем загруженные узлы в наше пространство имен
                for (int i = 0; i < predefinedNodes.Count; i++)
                {
                    AddPredefinedNode(SystemContext, predefinedNodes[i]);
                }

                // 3. Добавляем ссылку от базовой папки ObjectsFolder к нашему устройству
                ushort ns = NamespaceIndexes[0];
                var rootNodeId = new NodeId("PipetteDevice", ns);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, rootNodeId));

                AddReverseReferences(externalReferences);

                // 4. Привязываем C# логику к методам и переменным
                AttachLogicHandlers();
            }
        }

        private void AttachLogicHandlers()
        {
            ushort ns = NamespaceIndexes[0];

            // Находим метод ConfigurePipetting и привязываем делегат
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_ConfigurePipetting", ns), typeof(MethodState)) is MethodState configureMethod)
            {
                configureMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод StartPipetting
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_StartPipetting", ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Находим метод AbortPipetting
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_AbortPipetting", ns), typeof(MethodState)) is MethodState abortMethod)
            {
                abortMethod.OnCallMethod = Method_OnCall;
            }

            // Находим переменную Speed (доступна для записи) и привязываем перехват при изменении
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_Speed", ns), typeof(BaseDataVariableState)) is BaseDataVariableState speedVar)
            {
                speedVar.OnWriteValue = OnVariableWrite;
            }
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Pipette Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good;
        }

        // Реализация исполняемых методов (RPC из UaExpert)
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine("Button Pressed");
            // Дополнительный красивый вывод с названием метода
            Console.WriteLine($"[Pipette Remote Control]: Execute Command => '{method.BrowseName.Name}'");
            return StatusCodes.Good;
        }
    }
}
