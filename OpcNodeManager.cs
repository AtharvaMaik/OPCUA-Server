using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Server;
using MtpSimulator.Models;
using System.Globalization;

namespace MtpSimulator
{
    public class OpcNodeManager : CustomNodeManager2
    {
        private readonly IEnumerable<OpcUaItem> _items;
        private readonly List<BaseDataVariableState> _variables = new();  
        private readonly Dictionary<NodeId, double> _numState = new();     
        private CancellationTokenSource _simCts;
        private ushort _namespaceIndex;
        private FolderState _rootFolder;
        private readonly Random _rng = new();

        public OpcNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration,
            IEnumerable<OpcUaItem> items,
            string[] namespaceUris)
            : base(server, configuration, namespaceUris)
        {
            _items = items ?? Array.Empty<OpcUaItem>();
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            _namespaceIndex = (ushort)NamespaceIndexes[0];

            // Root folder for your namespace
            _rootFolder = CreateFolder(null, "PEADevices", "PEA Devices");

            // Create variables from items
            foreach (var item in _items)
            {
                try
                {
                    var v = CreateVariable(_rootFolder, item);
                    _variables.Add(v);
                }
                catch (Exception ex)
                {
                    Utils.Trace("Failed to create node for {0}: {1}",
                        item?.Identifier ?? item?.Id, ex.Message);
                }
            }

            // Link root folder under Objects
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
            {
                refs = new List<IReference>();
                externalReferences[ObjectIds.ObjectsFolder] = refs;
            }

            refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, _rootFolder.NodeId));
            _rootFolder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        }

        private FolderState CreateFolder(NodeState parent, string browseName, string displayName)
        {
            var folder = new FolderState(parent)
            {
                SymbolicName = browseName,
                NodeId = new NodeId(browseName, _namespaceIndex),
                BrowseName = new QualifiedName(browseName, _namespaceIndex),
                DisplayName = new LocalizedText(displayName),
                TypeDefinitionId = ObjectTypeIds.FolderType
            };

            if (parent == null)
                AddPredefinedNode(SystemContext, folder);
            else
                parent.AddChild(folder);

            return folder;
        }

        private BaseDataVariableState CreateVariable(FolderState parent, OpcUaItem item)
        {
            var id = item.Identifier ?? item.Id ?? Guid.NewGuid().ToString();
            var display = item.Name ?? item.Identifier ?? item.Id ?? id;
            var nodeId = new NodeId(id, _namespaceIndex);
            var browseName = new QualifiedName(id, _namespaceIndex);

            var dataTypeId = MapDataType(item.DataType);
            var access = MapAccessLevel(item.Access);

            var variable = new BaseDataVariableState(parent)
            {
                NodeId = nodeId,
                BrowseName = browseName,
                DisplayName = new LocalizedText(display),
                SymbolicName = id,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                ReferenceTypeId = ReferenceTypeIds.HasComponent, // better than Organizes for variables
                DataType = dataTypeId,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = access,
                UserAccessLevel = access,
                Historizing = false
            };

            // Initial value from AML (or defaults)
            var initial = ParseInitialValueOrDefault(item.DataType, item.InitialValue);
            variable.Value = initial;
            variable.StatusCode = StatusCodes.Good;
            variable.Timestamp = DateTime.UtcNow;

            // Enforce write policy via handler
            variable.OnSimpleWriteValue = OnWriteValue;

            parent.AddChild(variable);
            AddPredefinedNode(SystemContext, variable);

            // Seed numeric state for random-walk
            if (variable.DataType == DataTypeIds.Double ||
                variable.DataType == DataTypeIds.Int32 ||
                variable.DataType == DataTypeIds.Int16 ||
                variable.DataType == DataTypeIds.Int64 ||
                variable.DataType == DataTypeIds.UInt32 ||
                variable.DataType == DataTypeIds.UInt64 ||
                variable.DataType == DataTypeIds.Byte)
            {
                try { _numState[variable.NodeId] = Convert.ToDouble(initial); } catch { _numState[variable.NodeId] = 0.0; }
            }

            // Notify initial value to subscribers (proper SDK pattern)
            variable.ClearChangeMasks(SystemContext, false);

            return variable;
        }

        // Enforce read-only vs writable; convert input; update value
        private ServiceResult OnWriteValue(ISystemContext context, NodeState node, ref object value)
        {
            if (node is not BaseDataVariableState v)
                return StatusCodes.BadNodeIdUnknown;

            // Enforce read/write: reject if not writable
            if ((v.UserAccessLevel & AccessLevels.CurrentWrite) == 0)
                return StatusCodes.BadNotWritable;

            // Only scalar supported here (extend if you need arrays)
            if (v.ValueRank != ValueRanks.Scalar && v.ValueRank != ValueRanks.Any)
                return StatusCodes.BadTypeMismatch;

            // Coerce incoming value to the node's declared UA DataType
            if (!TryCoerceToNodeType(value, v.DataType, out var coerced))
                return StatusCodes.BadTypeMismatch;

            // Apply & notify
            v.Value = coerced;
            v.StatusCode = StatusCodes.Good;
            v.Timestamp = DateTime.UtcNow;
            v.ClearChangeMasks(SystemContext, false);

            // Keep numeric random-walk seed aligned (best effort)
            try
            {
                if (_numState.ContainsKey(v.NodeId) && coerced is IConvertible)
                    _numState[v.NodeId] = Convert.ToDouble(coerced);
            }
            catch { /* ignore */ }

            return ServiceResult.Good;
        }

        // ---- Manual coercion without TypeInfo ----
        private static bool TryCoerceToNodeType(object input, NodeId dataTypeId, out object value)
        {
            value = null;

            BuiltInType t = DetermineBuiltInType(dataTypeId);

            try
            {
                switch (t)
                {
                    case BuiltInType.Boolean:
                        if (input is bool bb) { value = bb; return true; }
                        if (input is string sb && bool.TryParse(sb, out var b)) { value = b; return true; }
                        value = Convert.ToBoolean(input); return true;

                    case BuiltInType.Double:
                        if (input is double dd) { value = dd; return true; }
                        if (input is string sd)
                        {
                            double res;
                            if (double.TryParse(sd, NumberStyles.Float, CultureInfo.InvariantCulture, out res) ||
                                double.TryParse(sd, NumberStyles.Float, CultureInfo.CurrentCulture, out res))
                            { value = res; return true; }
                            return false;
                        }
                        // last-resort conversion (uses current culture)
                        value = Convert.ToDouble(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.Float:
                        if (input is float ff) { value = ff; return true; }
                        if (input is string sf)
                        {
                            float res;
                            if (float.TryParse(sf, NumberStyles.Float, CultureInfo.InvariantCulture, out res) ||
                                float.TryParse(sf, NumberStyles.Float, CultureInfo.CurrentCulture, out res))
                            { value = res; return true; }
                            return false;
                        }
                        value = Convert.ToSingle(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.Int16:
                        if (input is short i16) { value = i16; return true; }
                        if (input is string s16 && short.TryParse(s16, NumberStyles.Integer, CultureInfo.CurrentCulture, out var p16)) { value = p16; return true; }
                        value = Convert.ToInt16(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.UInt16:
                        if (input is ushort u16) { value = u16; return true; }
                        if (input is string su16 && ushort.TryParse(su16, NumberStyles.Integer, CultureInfo.CurrentCulture, out var pu16)) { value = pu16; return true; }
                        value = Convert.ToUInt16(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.Int32:
                        if (input is int i32) { value = i32; return true; }
                        if (input is string s32 && int.TryParse(s32, NumberStyles.Integer, CultureInfo.CurrentCulture, out var p32)) { value = p32; return true; }
                        value = Convert.ToInt32(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.UInt32:
                        if (input is uint u32) { value = u32; return true; }
                        if (input is string su32 && uint.TryParse(su32, NumberStyles.Integer, CultureInfo.CurrentCulture, out var pu32)) { value = pu32; return true; }
                        value = Convert.ToUInt32(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.Int64:
                        if (input is long i64) { value = i64; return true; }
                        if (input is string s64 && long.TryParse(s64, NumberStyles.Integer, CultureInfo.CurrentCulture, out var p64)) { value = p64; return true; }
                        value = Convert.ToInt64(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.UInt64:
                        if (input is ulong u64) { value = u64; return true; }
                        if (input is string su64 && ulong.TryParse(su64, NumberStyles.Integer, CultureInfo.CurrentCulture, out var pu64)) { value = pu64; return true; }
                        value = Convert.ToUInt64(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.Byte:
                        if (input is byte by) { value = by; return true; }
                        if (input is string sby && byte.TryParse(sby, NumberStyles.Integer, CultureInfo.CurrentCulture, out var pby)) { value = pby; return true; }
                        value = Convert.ToByte(input, CultureInfo.CurrentCulture); return true;

                    case BuiltInType.String:
                        value = input?.ToString() ?? string.Empty; return true;

                    case BuiltInType.DateTime:
                        if (input is DateTime dt)
                        {
                            value = dt;
                            return true;
                        }

                        if (input is string sdt)
                        {
                            if (DateTime.TryParse(sdt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedIso))
                            {
                                value = parsedIso;
                                return true;
                            }

                            if (DateTime.TryParse(sdt, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsedLocal))
                            {
                                value = parsedLocal;
                                return true;
                            }

                            return false;
                        }

                        return false;


                    default:
                        value = input; return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static BuiltInType DetermineBuiltInType(NodeId dataTypeId)
        {
            if (dataTypeId == DataTypeIds.Boolean) return BuiltInType.Boolean;
            if (dataTypeId == DataTypeIds.SByte) return BuiltInType.SByte;
            if (dataTypeId == DataTypeIds.Byte) return BuiltInType.Byte;
            if (dataTypeId == DataTypeIds.Int16) return BuiltInType.Int16;
            if (dataTypeId == DataTypeIds.UInt16) return BuiltInType.UInt16;
            if (dataTypeId == DataTypeIds.Int32) return BuiltInType.Int32;
            if (dataTypeId == DataTypeIds.UInt32) return BuiltInType.UInt32;
            if (dataTypeId == DataTypeIds.Int64) return BuiltInType.Int64;
            if (dataTypeId == DataTypeIds.UInt64) return BuiltInType.UInt64;
            if (dataTypeId == DataTypeIds.Float) return BuiltInType.Float;
            if (dataTypeId == DataTypeIds.Double) return BuiltInType.Double;
            if (dataTypeId == DataTypeIds.String) return BuiltInType.String;
            if (dataTypeId == DataTypeIds.DateTime) return BuiltInType.DateTime;
            // Fallback
            return BuiltInType.String;
        }

        // ---------------- PUBLIC: start/stop simulation ----------------------

        public void StartSimulation(int intervalMs = 1000)
        {
            StopSimulation();

            if (intervalMs < 100) intervalMs = 100;

            _simCts = new CancellationTokenSource();
            var ct = _simCts.Token;

            // Background loop
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        StepSimulation();
                    }
                    catch (Exception ex)
                    {
                        Utils.Trace("Simulation step error: {0}", ex.Message);
                    }

                    try { await Task.Delay(intervalMs, ct); }
                    catch (TaskCanceledException) { break; }
                }
            }, ct);
        }

        public void StopSimulation()
        {
            if (_simCts == null) return;
            try { _simCts.Cancel(); }
            catch { /* ignored */ }
            finally { _simCts.Dispose(); _simCts = null; }
        }

        // ---------------- CORE: one simulation tick --------------------------

        private void StepSimulation()
        {
            var now = DateTime.UtcNow;

            foreach (var v in _variables)
            {
                object newValue = v.Value;

                if (v.DataType == DataTypeIds.Boolean)
                {
                    // Flip boolean ~30% of the time
                    bool cur = v.Value is bool b && b;
                    newValue = (_rng.NextDouble() < 0.3) ? !cur : cur;
                }
                else if (v.DataType == DataTypeIds.Double)
                {
                    // Bounded random walk in [0..100]
                    double cur = _numState.TryGetValue(v.NodeId, out var curD) ? curD : 0.0;
                    cur += (_rng.NextDouble() - 0.5) * 5.0; // step ~[-2.5..2.5]
                    cur = Math.Max(0.0, Math.Min(100.0, cur));
                    _numState[v.NodeId] = cur;
                    newValue = cur;
                }
                else if (v.DataType == DataTypeIds.Int32 || v.DataType == DataTypeIds.Int16 || v.DataType == DataTypeIds.Int64)
                {
                    double cur = _numState.TryGetValue(v.NodeId, out var curN) ? curN : 0.0;
                    cur += (_rng.NextDouble() - 0.5) * 3.0;
                    cur = Math.Max(0.0, Math.Min(1000.0, cur));
                    _numState[v.NodeId] = cur;

                    if (v.DataType == DataTypeIds.Int16) newValue = (short)Math.Round(cur);
                    else if (v.DataType == DataTypeIds.Int64) newValue = (long)Math.Round(cur);
                    else newValue = (int)Math.Round(cur);
                }
                else if (v.DataType == DataTypeIds.UInt32)
                {
                    double cur = _numState.TryGetValue(v.NodeId, out var curU32) ? curU32 : 0.0;
                    cur += (_rng.NextDouble() - 0.5) * 3.0;
                    cur = Math.Max(0.0, Math.Min(uint.MaxValue, cur));
                    _numState[v.NodeId] = cur;
                    newValue = (uint)Math.Round(cur);
                }
                else if (v.DataType == DataTypeIds.UInt64)
                {
                    double cur = _numState.TryGetValue(v.NodeId, out var curU64) ? curU64 : 0.0;
                    cur += (_rng.NextDouble() - 0.5) * 3.0;
                    cur = Math.Max(0.0, Math.Min((double)ulong.MaxValue, cur));
                    _numState[v.NodeId] = cur;
                    newValue = (ulong)Math.Round(cur);
                }
                else if (v.DataType == DataTypeIds.Byte)
                {
                    double cur = _numState.TryGetValue(v.NodeId, out var curBy) ? curBy : 0.0;
                    cur += (_rng.NextDouble() - 0.5) * 2.0;
                    cur = Math.Max(0.0, Math.Min(255.0, cur));
                    _numState[v.NodeId] = cur;
                    newValue = (byte)Math.Round(cur);
                }
                else if (v.DataType == DataTypeIds.DateTime)
                {
                    newValue = now;
                }
                else if (v.DataType == DataTypeIds.String)
                {
                    // Heartbeat string
                    newValue = $"{v.DisplayName?.Text ?? v.SymbolicName} @ {now:HH:mm:ss}";
                }
                else
                {
                    // default: leave as-is
                    continue;
                }

                // Apply + notify
                v.Value = newValue;
                v.StatusCode = StatusCodes.Good;
                v.Timestamp = now;

                // Proper change notification
                v.ClearChangeMasks(SystemContext, false);
            }
        }

        // ---------------- helpers --------------------------------------------

        private NodeId MapDataType(string amlDataType)
        {
            if (string.IsNullOrWhiteSpace(amlDataType)) return DataTypeIds.String;
            var s = amlDataType.Trim().ToLowerInvariant();

            if (s.Contains("bool")) return DataTypeIds.Boolean;
            if (s.Contains("double") || s.Contains("real") || s.Contains("float")) return DataTypeIds.Double;
            if (s.Contains("byte")) return DataTypeIds.Byte;
            if (s.Contains("int64") || s.Contains("long")) return DataTypeIds.Int64;
            if (s.Contains("uint64") || s.Contains("ulong")) return DataTypeIds.UInt64;
            if (s.Contains("uint32") || s.Contains("uint")) return DataTypeIds.UInt32;
            if (s.Contains("int32") || s == "int" || s.Contains("integer")) return DataTypeIds.Int32;
            if (s.Contains("int16") || s.Contains("short")) return DataTypeIds.Int16;
            if (s.Contains("date") || s.Contains("time")) return DataTypeIds.DateTime;

            return DataTypeIds.String;
        }

        private object ParseInitialValueOrDefault(string amlDataType, string raw)
        {
            var dt = MapDataType(amlDataType);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var s = raw.Trim();
                try
                {
                    if (dt == DataTypeIds.Boolean && bool.TryParse(s, out var b)) return b;
                    if (dt == DataTypeIds.Double && double.TryParse(s, out var d)) return d;
                    if (dt == DataTypeIds.Int64 && long.TryParse(s, out var i64)) return i64;
                    if (dt == DataTypeIds.UInt64 && ulong.TryParse(s, out var u64)) return (ulong)u64;
                    if (dt == DataTypeIds.UInt32 && uint.TryParse(s, out var u32)) return (uint)u32;
                    if (dt == DataTypeIds.Int32 && int.TryParse(s, out var i32)) return i32;
                    if (dt == DataTypeIds.Int16 && short.TryParse(s, out var i16)) return i16;
                    if (dt == DataTypeIds.Byte && byte.TryParse(s, out var by)) return by;
                    if (dt == DataTypeIds.DateTime && DateTime.TryParse(s, out var t)) return t;
                    return s;
                }
                catch
                {
                    return s;
                }
            }

            if (dt == DataTypeIds.Boolean) return false;
            if (dt == DataTypeIds.Double) return 0.0;
            if (dt == DataTypeIds.Int64) return 0L;
            if (dt == DataTypeIds.UInt64) return 0UL;
            if (dt == DataTypeIds.UInt32) return 0U;
            if (dt == DataTypeIds.Int32) return 0;
            if (dt == DataTypeIds.Int16) return (short)0;
            if (dt == DataTypeIds.Byte) return (byte)0;
            if (dt == DataTypeIds.DateTime) return DateTime.UtcNow;
            return string.Empty;
        }

        private byte MapAccessLevel(int access) =>
            access switch
            {
                1 => (byte)AccessLevels.CurrentRead,
                2 => (byte)AccessLevels.CurrentWrite,
                3 => (byte)AccessLevels.CurrentReadOrWrite,
                _ => (byte)AccessLevels.CurrentRead
            };
    }
}

