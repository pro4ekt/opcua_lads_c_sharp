using Opc.Ua;
using Opc.Ua.Server;

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

                ushort ns = NamespaceIndexes[0];

                // 1. Корневой объект: PipetteDevice
                FolderState pipetteDevice = new FolderState(null)
                {
                    NodeId = new NodeId("PipetteDevice", ns),
                    BrowseName = new QualifiedName("PipetteDevice", ns),
                    DisplayName = new LocalizedText("PipetteDevice"),
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    EventNotifier = EventNotifiers.None
                };
                AddPredefinedNode(SystemContext, pipetteDevice);
                // Привязываем корневую папку к ObjectsFolder (Address Space)
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, pipetteDevice.NodeId));

                // Вспомогательный метод для быстрого создания переменных
                BaseDataVariableState CreateVariable(NodeState parent, string name, NodeId dataType, object initialValue)
                {
                    var variable = new BaseDataVariableState(parent)
                    {
                        NodeId = new NodeId(parent.NodeId.Identifier + "_" + name, ns),
                        BrowseName = new QualifiedName(name, ns),
                        DisplayName = new LocalizedText(name),
                        DataType = dataType,
                        ValueRank = ValueRanks.Scalar,
                        AccessLevel = AccessLevels.CurrentRead,
                        UserAccessLevel = AccessLevels.CurrentRead,
                        Value = initialValue
                    };
                    parent.AddChild(variable);
                    AddPredefinedNode(SystemContext, variable);
                    return variable;
                }

                // Переменные для PipetteDevice
                CreateVariable(pipetteDevice, "DeviceId", DataTypeIds.String, "PIP-001");
                CreateVariable(pipetteDevice, "Model", DataTypeIds.String, "LADS-Pipette-V1");
                CreateVariable(pipetteDevice, "MinVolume", DataTypeIds.Double, 0.5);
                CreateVariable(pipetteDevice, "MaxVolume", DataTypeIds.Double, 10.0);
                CreateVariable(pipetteDevice, "BatteryLevel", DataTypeIds.UInt16, (ushort)100);
                CreateVariable(pipetteDevice, "CurrentState", DataTypeIds.String, "Idle");

                // 2. Дочерний объект: ChannelComponent
                FolderState channelComponent = new FolderState(pipetteDevice)
                {
                    NodeId = new NodeId("PipetteDevice_ChannelComponent", ns),
                    BrowseName = new QualifiedName("ChannelComponent", ns),
                    DisplayName = new LocalizedText("ChannelComponent"),
                    TypeDefinitionId = ObjectTypeIds.FolderType
                };
                pipetteDevice.AddChild(channelComponent);
                AddPredefinedNode(SystemContext, channelComponent);

                CreateVariable(channelComponent, "ChannelId", DataTypeIds.UInt16, (ushort)1);
                CreateVariable(channelComponent, "CurrentVolumeSetting", DataTypeIds.Double, 5.0);
                CreateVariable(channelComponent, "TipPresent", DataTypeIds.Boolean, false);

                // 3. Дочерний объект: BatteryComponent
                FolderState batteryComponent = new FolderState(pipetteDevice)
                {
                    NodeId = new NodeId("PipetteDevice_BatteryComponent", ns),
                    BrowseName = new QualifiedName("BatteryComponent", ns),
                    DisplayName = new LocalizedText("BatteryComponent"),
                    TypeDefinitionId = ObjectTypeIds.FolderType
                };
                pipetteDevice.AddChild(batteryComponent);
                AddPredefinedNode(SystemContext, batteryComponent);

                CreateVariable(batteryComponent, "BatteryLevel", DataTypeIds.UInt16, (ushort)100);
                CreateVariable(batteryComponent, "Charging", DataTypeIds.Boolean, false);

                // 4. Дочерний объект: PipettingFunction
                FolderState pipettingFunction = new FolderState(pipetteDevice)
                {
                    NodeId = new NodeId("PipetteDevice_PipettingFunction", ns),
                    BrowseName = new QualifiedName("PipettingFunction", ns),
                    DisplayName = new LocalizedText("PipettingFunction"),
                    TypeDefinitionId = ObjectTypeIds.FolderType
                };
                pipetteDevice.AddChild(pipettingFunction);
                AddPredefinedNode(SystemContext, pipettingFunction);

                CreateVariable(pipettingFunction, "TargetVolume", DataTypeIds.Double, 5.0);
                CreateVariable(pipettingFunction, "Mode", DataTypeIds.String, "Aspirate");
                CreateVariable(pipettingFunction, "Speed", DataTypeIds.UInt16, (ushort)50);
                CreateVariable(pipettingFunction, "PipettingState", DataTypeIds.String, "Idle");
                CreateVariable(pipettingFunction, "Progress", DataTypeIds.UInt16, (ushort)0);

                // Вспомогательный метод для быстрого создания методов (RPC)
                MethodState CreateMethod(NodeState parent, string methodName)
                {
                    var method = new MethodState(parent)
                    {
                        NodeId = new NodeId(parent.NodeId.Identifier + "_" + methodName, ns),
                        BrowseName = new QualifiedName(methodName, ns),
                        DisplayName = new LocalizedText(methodName),
                        Executable = true,
                        UserExecutable = true,
                        // Подвязываем реализацию делегата:
                        OnCallMethod = Method_OnCall 
                    };
                    parent.AddChild(method);
                    AddPredefinedNode(SystemContext, method);
                    return method;
                }

                // Методы для PipettingFunction
                CreateMethod(pipettingFunction, "ConfigurePipetting");
                CreateMethod(pipettingFunction, "StartPipetting");
                CreateMethod(pipettingFunction, "AbortPipetting");

                // Строим обратные связи (Parent -> Child и т.д.)
                AddReverseReferences(externalReferences);
            }
        }

        // Реализация (заглушка) исполняемых методов
        private ServiceResult Method_OnCall(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine($"[Pipette] Method '{method.BrowseName.Name}' called/executed!");
            return StatusCodes.Good;
        }
    }
}
