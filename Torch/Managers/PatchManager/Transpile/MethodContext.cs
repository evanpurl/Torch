using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Torch.Managers.PatchManager.MSIL;
using Torch.Utils;
using VRage.Game.VisualScripting.Utils;

namespace Torch.Managers.PatchManager.Transpile
{
    internal class MethodContext
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public readonly MethodBase Method;
        public readonly MethodBody MethodBody;
        private readonly byte[] _msilBytes;

        internal Dictionary<int, MsilLabel> Labels { get; } = new Dictionary<int, MsilLabel>();
        private readonly List<MsilInstruction> _instructions = new List<MsilInstruction>();
        public IReadOnlyList<MsilInstruction> Instructions => _instructions;

        internal ITokenResolver TokenResolver { get; }

        internal MsilLabel LabelAt(int i)
        {
            if (Labels.TryGetValue(i, out MsilLabel label))
                return label;
            Labels.Add(i, label = new MsilLabel());
            return label;
        }

        public MethodContext(MethodBase method)
        {
            Method = method;
            MethodBody = method.GetMethodBody();
            Debug.Assert(MethodBody != null, "Method body is null");
            _msilBytes = MethodBody.GetILAsByteArray();
            TokenResolver = new NormalTokenResolver(method);
        }


#pragma warning disable 649
        [ReflectedMethod(Name = "BakeByteArray")]
        private static Func<ILGenerator, byte[]> _ilGeneratorBakeByteArray;

        [ReflectedMethod(Name = "GetExceptions")]
        private static Func<ILGenerator, Array> _ilGeneratorGetExceptionHandlers;

        private const string InternalExceptionInfo = "System.Reflection.Emit.__ExceptionInfo, mscorlib";

        [ReflectedMethod(Name = "GetExceptionTypes", TypeName = InternalExceptionInfo)]
        private static Func<object, int[]> _exceptionHandlerGetTypes;

        [ReflectedMethod(Name = "GetStartAddress", TypeName = InternalExceptionInfo)]
        private static Func<object, int> _exceptionHandlerGetStart;

        [ReflectedMethod(Name = "GetEndAddress", TypeName = InternalExceptionInfo)]
        private static Func<object, int> _exceptionHandlerGetEnd;

        [ReflectedMethod(Name = "GetFinallyEndAddress", TypeName = InternalExceptionInfo)]
        private static Func<object, int> _exceptionHandlerGetFinallyEnd;

        [ReflectedMethod(Name = "GetNumberOfCatches", TypeName = InternalExceptionInfo)]
        private static Func<object, int> _exceptionHandlerGetCatchCount;

        [ReflectedMethod(Name = "GetCatchAddresses", TypeName = InternalExceptionInfo)]
        private static Func<object, int[]> _exceptionHandlerGetCatchAddrs;

        [ReflectedMethod(Name = "GetCatchEndAddresses", TypeName = InternalExceptionInfo)]
        private static Func<object, int[]> _exceptionHandlerGetCatchEndAddrs;

        [ReflectedMethod(Name = "GetFilterAddresses", TypeName = InternalExceptionInfo)]
        private static Func<object, int[]> _exceptionHandlerGetFilterAddrs;
#pragma warning restore 649

        private readonly Array _dynamicExceptionTable;

        public MethodContext(DynamicMethod method)
        {
            // Keep a reference so callers can log Method if needed
            Method = method;
            MethodBody = null;

            var ilGen = method.GetILGenerator();

            // Default to "no IL" and "no exceptions"
            _msilBytes = Array.Empty<byte>();
            _dynamicExceptionTable = null;
            TokenResolver = new NoopTokenResolver();

            // If we have the private hooks, try to bake IL
            if (_ilGeneratorBakeByteArray != null)
            {
                var bytes = _ilGeneratorBakeByteArray(ilGen) ?? Array.Empty<byte>();
                _msilBytes = bytes;

                // Only construct DynamicMethodTokenResolver if IL is finalized (non-empty)
                if (bytes.Length > 0)
                {
                    // Safe to use the dynamic resolver now
                    TokenResolver = new DynamicMethodTokenResolver(method);
                }
            }

            // Try to read exception handlers only if the private hook exists
            if (_ilGeneratorGetExceptionHandlers != null)
            {
                _dynamicExceptionTable = _ilGeneratorGetExceptionHandlers(ilGen);
            }
        }



        public void Read()
        {
            ReadInstructions();
            ResolveLabels();
            ResolveCatchClauses();
        }

        private void ReadInstructions()
        {
            Labels.Clear();
            _instructions.Clear();

            // Guard against missing or empty IL
            if (_msilBytes == null || _msilBytes.Length == 0)
            {
                _log.Debug("No IL bytes available; skipping instruction read.");
                return; // Nothing to read (DynamicMethod or stripped IL)
            }

            using var memory = new MemoryStream(_msilBytes, writable: false);
            using var reader = new BinaryReader(memory, Encoding.Latin1, leaveOpen: false);


            while (memory.Position < memory.Length)
            {
                int opcodeOffset = (int)memory.Position;

                // Read first byte; if end of stream, break
                int firstByte = reader.Read();
                if (firstByte == -1)
                    break;

                short instructionValue = (short)firstByte;

                // Handle two-byte opcodes (0xFE prefix)
                if (Prefixes.Contains(instructionValue))
                {
                    int nextByte = reader.Read();
                    if (nextByte == -1)
                        break; // Malformed/truncated IL
                    instructionValue = (short)((instructionValue << 8) | nextByte);
                }

                if (!OpCodeLookup.TryGetValue(instructionValue, out OpCode opcode))
                {
                    string msg = $"Unknown or invalid opcode 0x{instructionValue:X} at offset 0x{opcodeOffset:X4}.";
                    Console.WriteLine(msg);
                    continue;
                }

                // Sanity check: opcode size is advisory; just skip invalid bounds
                if (opcode.Size > memory.Length - opcodeOffset)
                {
                    Console.WriteLine($"Opcode {opcode} size {opcode.Size} exceeds remaining IL bytes; stopping read.");
                    break;
                }

                var instruction = new MsilInstruction(opcode)
                {
                    Offset = opcodeOffset
                };

                _instructions.Add(instruction);

                // Safely parse operand if applicable
                try
                {
                    instruction.Operand?.Read(this, reader);
                }
                catch (EndOfStreamException)
                {
                    Console.WriteLine($"Truncated operand for opcode {opcode} at offset 0x{opcodeOffset:X4}; stopping read.");
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Failed to parse operand for {opcode} at offset 0x{opcodeOffset:X4}");
                }
            }
        }


        private sealed class NoopTokenResolver : ITokenResolver
        {
            public FieldInfo ResolveField(int token) => throw new NotSupportedException("No tokens for dynamic method without finalized IL.");
            public MemberInfo ResolveMember(int token) => throw new NotSupportedException("No tokens for dynamic method without finalized IL.");
            public MethodBase ResolveMethod(int token) => throw new NotSupportedException("No tokens for dynamic method without finalized IL.");

            public byte[] ResolveSignature(int token)
            {
                throw new NotImplementedException();
            }

            public string ResolveString(int token) => throw new NotSupportedException("No tokens for dynamic method without finalized IL.");
            public Type ResolveType(int token) => throw new NotSupportedException("No tokens for dynamic method without finalized IL.");
        }

        private void ResolveCatchClauses()
        {
            if (MethodBody != null)
                foreach (var clause in MethodBody.ExceptionHandlingClauses)
                {
                    AddEhHandler(clause.TryOffset, MsilTryCatchOperationType.BeginExceptionBlock);
                    if ((clause.Flags & ExceptionHandlingClauseOptions.Fault) != 0)
                        AddEhHandler(clause.HandlerOffset, MsilTryCatchOperationType.BeginFaultBlock);
                    else if ((clause.Flags & ExceptionHandlingClauseOptions.Finally) != 0)
                        AddEhHandler(clause.HandlerOffset, MsilTryCatchOperationType.BeginFinallyBlock);
                    else
                        AddEhHandler(clause.HandlerOffset, MsilTryCatchOperationType.BeginClauseBlock, clause.CatchType);
                    AddEhHandler(clause.HandlerOffset + clause.HandlerLength, MsilTryCatchOperationType.EndExceptionBlock);
                }

            if (_dynamicExceptionTable != null)
                foreach (var eh in _dynamicExceptionTable)
                {
                    var catchCount = _exceptionHandlerGetCatchCount(eh);
                    var exTypes = _exceptionHandlerGetTypes(eh);
                    var exCatches = _exceptionHandlerGetCatchAddrs(eh);
                    var exCatchesEnd = _exceptionHandlerGetCatchEndAddrs(eh);
                    var exFilters = _exceptionHandlerGetFilterAddrs(eh);
                    var tryAddr = _exceptionHandlerGetStart(eh);
                    var endAddr = _exceptionHandlerGetEnd(eh);
                    var endFinallyAddr = _exceptionHandlerGetFinallyEnd(eh);
                    for (var i = 0; i < catchCount; i++)
                    {
                        var flags = (ExceptionHandlingClauseOptions) exTypes[i];
                        var endAddress = (flags & ExceptionHandlingClauseOptions.Finally) != 0 ? endFinallyAddr : endAddr;

                        var catchAddr = exCatches[i];
                        var catchEndAddr = exCatchesEnd[i];
                        var filterAddr = exFilters[i];
                        
                        AddEhHandler(tryAddr, MsilTryCatchOperationType.BeginExceptionBlock);
                        if ((flags & ExceptionHandlingClauseOptions.Fault) != 0)
                            AddEhHandler(catchAddr, MsilTryCatchOperationType.BeginFaultBlock);
                        else if ((flags & ExceptionHandlingClauseOptions.Finally) != 0)
                            AddEhHandler(catchAddr, MsilTryCatchOperationType.BeginFinallyBlock);
                        else
                            AddEhHandler(catchAddr, MsilTryCatchOperationType.BeginClauseBlock);
                        AddEhHandler(catchEndAddr, MsilTryCatchOperationType.EndExceptionBlock);
                    }
                }
        }

        private void AddEhHandler(int offset, MsilTryCatchOperationType op, Type type = null)
        {
            var instruction = FindInstruction(offset);
            instruction.TryCatchOperations.Add(new MsilTryCatchOperation(op, type) {NativeOffset = offset});
            instruction.TryCatchOperations.Sort((a, b) => a.NativeOffset.CompareTo(b.NativeOffset));
        }

        public MsilInstruction FindInstruction(int offset)
        {
            int min = 0, max = _instructions.Count;
            while (min != max)
            {
                int mid = (min + max) / 2;
                if (_instructions[mid].Offset < offset)
                    min = mid + 1;
                else
                    max = mid;
            }

            return min >= 0 && min < _instructions.Count ? _instructions[min] : null;
        }

        private void ResolveLabels()
        {
            foreach (var label in Labels)
            {
                MsilInstruction target = FindInstruction(label.Key);
                Debug.Assert(target != null, $"No label for offset {label.Key}");
                target?.Labels?.Add(label.Value);
            }
        }


        public string ToHumanMsil()
        {
            return string.Join("\n", _instructions.Select(x => $"IL_{x.Offset:X4}: {x.StackChange():+0;-#} {x}"));
        }

        private static readonly Dictionary<short, OpCode> OpCodeLookup;
        private static readonly HashSet<short> Prefixes;

        static MethodContext()
        {
            OpCodeLookup = new Dictionary<short, OpCode>();
            Prefixes = new HashSet<short>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                var opcode = (OpCode) field.GetValue(null);
                if (opcode.OpCodeType != OpCodeType.Nternal)
                    OpCodeLookup.Add(opcode.Value, opcode);
                if ((ushort) opcode.Value > 0xFF)
                {
                    Prefixes.Add((short) ((ushort) opcode.Value >> 8));
                }
            }
        }
    }
}