using Opc.Ua;
using Opc.Ua.Server;
using System.IO;
using Opc.Ua.Export;
using System.Threading;
using System.Threading.Tasks;

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
        
        // Добавляем поля для хранения ссылок на узлы переменных и токен отмены задачи
        private CancellationTokenSource _spinningCts;
        
        /// <summary>
        /// Конструктор NodeManager-а для нашего устройства. 
        /// </summary>
        public CentrifugeNodeManager(IServerInternal server, ApplicationConfiguration configuration) 
            : base(server, configuration, 
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/")
        {
            SystemContext.NodeIdFactory = this; // Говорим контексту, что этот менеджер будет создавать NodeId
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://lab.server/Centrifuge/"
            ];
        }
        
        /// <summary>
        /// Этот метод вызывается ядром сервера при его старте. 
        /// </summary>
        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                // Убеждаемся, что в словаре есть базовая папка ObjectsFolder
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                // Импортируем зависимости Opc.Ua (DI -> AMB -> Machinery -> LADS)
                ImportXmlResource(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
                ImportXmlResource(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.AMB.NodeSet2.xml");
                ImportXmlResource(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
                ImportXmlResource(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");

                // 1. Читаем структуру из XML файла (ПАРСИНГ)
                string nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "NodeSet", "Centrifuge.xml");
                if (!File.Exists(nodeSetFilePath))
                {
                    // Попытка найти рядом с exe, если запущено не из студии (после публикации)
                    nodeSetFilePath = Path.Combine(AppContext.BaseDirectory, "NodeSet", "Centrifuge.xml");
                }

                // Коллекция для временного хранения объектов, загруженных из файла
                NodeStateCollection predefinedNodes = new NodeStateCollection();
                using (Stream stream = File.OpenRead(nodeSetFilePath))
                {
                    UANodeSet nodeSet = UANodeSet.Read(stream);
                    foreach (var nameSpace in nodeSet.NamespaceUris)
                    {
                        SystemContext.NamespaceUris.GetIndexOrAppend(nameSpace);
                    }
                    nodeSet.Import(SystemContext, predefinedNodes);
                }

                var toImportNodes = new List<NodeState>();
                for (int i = 0; i < predefinedNodes.Count; i++)
                {
                    var node = predefinedNodes[i];
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

                // 3. Создаем связь между стандартным корнем сервера (ObjectsFolder) и нашим главным устройством
                ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");
                var rootNodeId = new NodeId("Centrifuge", ns);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, rootNodeId));

                // Создаем обратные ссылки
                AddReverseReferences(externalReferences);

                // 4. Привязываем C# логику
                AttachLogicHandlers();
            }
        }

        private void ImportXmlResource(IDictionary<NodeId, IList<IReference>> externalReferences, string resourcePath)
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath);
            if (stream == null) return;
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
        
        /// <summary>
        /// Вызывается автоматически, когда клиент (مثلاً, UaExpert) изменяет значение переменной и отправляет его серверу.
        /// </summary>
        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Variable '{node.BrowseName.Name}' updated to '{value}' by client.");
            return StatusCodes.Good; // Подтверждаем клиенту, что запись прошла успешно
        }
        
        /// <summary>
        /// В этом методе мы "оживляем" простое дерево объектов, привязывая делегаты (коллбеки)
        /// к переменным и методам, которые были загружены из XML.
        /// Без этого метода устройство было бы только для чтения (или методы просто ничего бы не делали).
        /// </summary>
        private void AttachLogicHandlers()
        {
            ushort ns = SystemContext.NamespaceUris.GetIndexOrAppend("http://lab.server/Centrifuge/");

            // Находим метод StartProgram (в XML это i=7017)
            if (FindPredefinedNode(new NodeId(7017u, ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Привязываем коллбек на изменение переменной AssetId (в XML это i=6018)
            if (FindPredefinedNode(new NodeId(6018u, ns), typeof(BaseVariableState)) is BaseVariableState assetIdVar)
            {
                assetIdVar.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
            }
        }
        
        /// <summary>
        /// Вызывается автоматически, когда клиент запускает RPC-метод на сервере.
        /// </summary>
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartProgram")
            {
                OnStartProgramCalled?.Invoke();
                StartSpinningTask();
            }
            else if (method.BrowseName.Name == "StopProgram")
            {
                // Останавливаем фоновый процесс, если нажали Abort
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
            
            Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        Console.WriteLine("[Centrifuge]: Spinning...");
                        await Task.Delay(1000, token); // Имитируем работу центрифуги (каждую секунду выводим статус)
                    }
                }
                catch (TaskCanceledException)
                {
                    // Задача была отменена токеном
                } 
            }, token);
        }
    }
}
