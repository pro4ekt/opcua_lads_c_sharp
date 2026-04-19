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
            // Тут можно передать команду на "железо", чтобы пипетка сменила скорость
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

           /*  // Находим метод ConfigurePipetting по его NodeId (из XML) и привязываем C# делегат
            if (FindPredefinedNode(new NodeId("PipetteDevice_PipettingFunction_ConfigurePipetting", ns), typeof(MethodState)) is MethodState configureMethod)
            {
                configureMethod.OnCallMethod = Method_OnCall;
            }
            */

            // Находим метод StartSpinning
            if (FindPredefinedNode(new NodeId("Centrifuge_Functions_StartSpinning", ns), typeof(MethodState)) is MethodState startMethod)
            {
                startMethod.OnCallMethod = Method_OnCall;
            }

            // Находим StopSpinning
            if (FindPredefinedNode(new NodeId("Centrifuge_Functions_StopSpinning", ns), typeof(MethodState)) is MethodState abortMethod)
            {
                abortMethod.OnCallMethod = Method_OnCall;
            }
        }
        
        /// <summary>
        /// Вызывается автоматически, когда клиент запускает RPC-метод на сервере.
        /// </summary>
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Centrifuge Remote Control]: Execute Command => '{method.BrowseName.Name}'");

            if (method.BrowseName.Name == "StartSpinning")
            {
                StartSpinningTask();
            }
            else if (method.BrowseName.Name == "StopSpinning")
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
