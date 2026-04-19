using Opc.Ua.Server;
using Opc.Ua;
using Opc.Ua.Export;
using System.Reflection;
using Opc.Ua.LADS;
using ObjectIds = Opc.Ua.ObjectIds;

namespace OpcUa.Lads.Foundation.Server
{
    public sealed class NodeManager : CustomNodeManager2
    {
        private Controller _controller;

        public NodeManager(IServerInternal server, ApplicationConfiguration configuration,
            params string[] namespaceUris) : base(server, configuration, namespaceUris)
        {
            SystemContext.NodeIdFactory = this;
            NamespaceUris =
            [
                "http://opcfoundation.org/UA/DI/",
                "http://opcfoundation.org/UA/AMB/",
                "http://opcfoundation.org/UA/Machinery/",
                "http://opcfoundation.org/UA/LADS/",
                "http://spectaris.de/LuminescenceReader/"
            ];
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out _))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = [];
                }

                ImportXml(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.DI.NodeSet2.xml");
                ImportXml(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.AMB.NodeSet2.xml");
                ImportXml(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.Machinery.NodeSet2.xml");
                ImportXml(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.Opc.Ua.LADS.NodeSet2.xml");
                ImportXml(externalReferences, "OpcUa.Lads.Foundation.Server.NodeSet.LuminescenceReader.xml");

                // Attach children to parent (missing when not using the builtin load function)
                foreach (var node in PredefinedNodes)
                {
                    if (node.Value.Handle != null && PredefinedNodes.TryGetValue((NodeId)node.Value.Handle, out var parent))
                    {
                        var child = (BaseInstanceState)node.Value;
                        // UANodeSet import already added the references, so AddChild will duplicate them.
                        // Removing existing references first avoids duplication in clients.
                        if (child.ReferenceTypeId != null)
                        {
                            parent.RemoveReference(child.ReferenceTypeId, false, child.NodeId);
                            child.RemoveReference(child.ReferenceTypeId, true, parent.NodeId);
                        }
                        
                        parent.AddChild(child);
                    }
                }

                var passiveTemp = (BaseObjectState)PredefinedNodes.First(n =>
                    (uint)n.Key.Identifier == Spectaris.LuminescenceReader.Objects
                        .LuminescenceReaderDevice_FunctionalUnitSet_LuminescenceReaderUnit_FunctionSet_TemperatureController &&
                    n.Value.DisplayName == "Temperature Controller").Value;
                var activeTemp = new AnalogControlFunctionTypeState(passiveTemp.Parent);
                UpdateInstance(passiveTemp, activeTemp);

                var passiveSensor = (BaseObjectState)PredefinedNodes.First(n =>
                    (uint)n.Key.Identifier == Spectaris.LuminescenceReader.Objects
                        .LuminescenceReaderDevice_FunctionalUnitSet_LuminescenceReaderUnit_FunctionSet_LuminescenceSensor &&
                    n.Value.DisplayName == "Luminescence Sensor").Value;
                var activeSensor = new AnalogArraySensorFunctionTypeState(passiveSensor.Parent);
                UpdateInstance(passiveSensor, activeSensor);

                _controller = new Controller(SystemContext);
                _controller.Init(activeTemp, activeSensor);
                _controller.Start();
            }
        }

        private void UpdateInstance(BaseObjectState passive, BaseObjectState active)
        {
            active.Create(SystemContext, passive);
            passive.Parent?.ReplaceChild(SystemContext, active);
            AddPredefinedNode(SystemContext, active);
        }

        private void ImportXml(IDictionary<NodeId, IList<IReference>> externalReferences, string resourcePath)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath);
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
                // The LADS node set is not ordered :(
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

            // Ensure the reverse references exist.
            AddReverseReferences(externalReferences);

            // Hook StartProgram methods
            foreach (var node in PredefinedNodes.Values.ToList())
            {
                if (node is MethodState method && method.BrowseName != null)
                {
                    if (method.BrowseName.Name == "StartProgram")
                    {
                        // Override the framework's strict input validation
                        method.OnCallMethod = new GenericMethodCalledEventHandler(OnStartProgramCalled);

                        // LADS nodeset bug: some StartProgram instances don't have InputArguments
                        if (method.InputArguments == null)
                        {
                            var inputArguments = new PropertyState<Argument[]>(method)
                            {
                                NodeId = new NodeId(Guid.NewGuid(), method.NodeId.NamespaceIndex),
                                BrowseName = Opc.Ua.BrowseNames.InputArguments,
                                DisplayName = Opc.Ua.BrowseNames.InputArguments,
                                TypeDefinitionId = Opc.Ua.VariableTypeIds.PropertyType,
                                ReferenceTypeId = Opc.Ua.ReferenceTypeIds.HasProperty,
                                DataType = Opc.Ua.DataTypeIds.Argument,
                                ValueRank = ValueRanks.OneDimension,
                                Value = new Argument[]
                                {
                                    new Argument { Name = "Properties", DataType = NodeId.Parse("ns=4;i=3003"), ValueRank = ValueRanks.OneDimension, Description = "A Key/Value set for parameterization of the program." },
                                    new Argument { Name = "SupervisoryJobId", DataType = Opc.Ua.DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = "" },
                                    new Argument { Name = "SupervisoryTaskId", DataType = Opc.Ua.DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = "The ID of the SupervisoryTask." },
                                    new Argument { Name = "Samples", DataType = NodeId.Parse("ns=4;i=3002"), ValueRank = ValueRanks.OneDimension, Description = "An array of the SampleInfoType that describes the samples processed in this program execution." },
                                    new Argument { Name = "ProgramTemplateId", DataType = Opc.Ua.DataTypeIds.String, ValueRank = ValueRanks.Scalar, Description = "The ID of the program template that is used for the current execution." }
                                }
                            };
                            method.InputArguments = inputArguments;
                            method.AddChild(inputArguments);
                            AddPredefinedNode(SystemContext, inputArguments);
                        }
                    }
                    else if (method.BrowseName.Name == "StopProgram" || method.BrowseName.Name == "Stop")
                    {
                        method.OnCallMethod = new GenericMethodCalledEventHandler(OnStopProgramCalled);
                    }
                }
                else if (node is BaseVariableState variable && variable.BrowseName != null)
                {
                    // Цепляем колбек на изменение переменной (например, SupervisoryJobId или AssetId)
                    if (variable.BrowseName.Name == "SupervisoryJobId" || variable.BrowseName.Name == "SupervisoryTaskId" || variable.BrowseName.Name == "AssetId")
                    {
                        variable.OnWriteValue = new NodeValueEventHandler(OnVariableWrite);
                    }
                }
            }
        }

        private ServiceResult OnVariableWrite(ISystemContext context, NodeState node, NumericRange indexRange, QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp)
        {
            Console.WriteLine($"\n[Variable Hook] Client is changing variable '{node.BrowseName.Name}'");
            Console.WriteLine($"[Variable Hook] New value will be: {value}");
            
            // Здесь вы можете добавить бизнес логику или отменить изменение (вернув ошибку)
            return ServiceResult.Good; // Good разрешает запись значения в память узла
        }

        private ServiceResult CustomAreMethodArgumentsValid(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            try
            {
                Console.WriteLine("\n[StartProgram Validation Debug]");
                if (method.InputArguments?.Value is Argument[] expectedArgs)
                {
                    Console.WriteLine($"Expected args: {expectedArgs.Length}, Received args: {inputArguments?.Count ?? 0}");
                    if (inputArguments != null)
                    {
                        for (int i = 0; i < inputArguments.Count; i++)
                        {
                            var argName = i < expectedArgs.Length ? expectedArgs[i].Name : $"Arg{i}";
                            var expectedType = i < expectedArgs.Length ? expectedArgs[i].DataType.ToString() : "N/A";
                            var actualType = inputArguments[i]?.GetType().FullName ?? "null";
                            
                            Console.WriteLine($"  [{i}] {argName}:");
                            Console.WriteLine($"      Expected TypeNode: {expectedType}");
                            Console.WriteLine($"      Actual Value Type: {actualType}");
                            if (inputArguments[i] is Array arr)
                            {
                                Console.WriteLine($"      Actual Array Length: {arr.Length}");
                                if (arr.Length > 0 && arr.GetValue(0) != null)
                                {
                                    Console.WriteLine($"      Element Type: {arr.GetValue(0).GetType().FullName}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"      Actual Value: {inputArguments[i]}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Validation Error Logger Failed: {ex.Message}");
            }
            
            // We printed the arguments. Since we handle the call here, return Good.
            return ServiceResult.Good;
        }

        private ServiceResult OnStartProgramCalled(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            CustomAreMethodArgumentsValid(context, method, inputArguments, outputArguments);
            Console.WriteLine("Program was started");
            return ServiceResult.Good;
        }

        private ServiceResult OnStopProgramCalled(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            Console.WriteLine("Program was stopped");
            return ServiceResult.Good;
        }

        protected override void Dispose(bool disposing)
        {
            _controller.Stop();
            base.Dispose(disposing);
        }
    }
}
