using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using NLog;
using Torch.Managers.PatchManager.MSIL;
using Torch.Managers.PatchManager.Transpile;
using Torch.Utils;

namespace Torch.Managers.PatchManager
{
    /// <summary>
    /// Functions that let you read and write MSIL to methods directly.
    /// </summary>
    public class PatchUtilities
    {
        /// <summary>
        /// Gets the content of a method as an instruction stream
        /// </summary>
        /// <param name="method">Method to examine</param>
        /// <returns>instruction stream</returns>
        public static IEnumerable<MsilInstruction> ReadInstructions(MethodBase method)
        {
            var context = new MethodContext(method);
            context.Read();
            return context.Instructions;
        }

        /// <summary>
        /// Writes the given instruction stream to the given IL generator, fixing short branch instructions.
        /// </summary>
        /// <param name="insn">Instruction stream</param>
        /// <param name="generator">Output</param>
        public static void EmitInstructions(IEnumerable<MsilInstruction> insn, LoggingIlGenerator generator)
        {
            MethodTranspiler.EmitMethod(insn.ToList(), generator);
        }

        public delegate void DelPrintIntegrityInfo(bool error, string msg);

        /// <summary>
        /// Analyzes the integrity of a set of instructions.
        /// </summary>
        /// <param name="handler">Logger</param>
        /// <param name="instructions">instructions</param>
        public static void IntegrityAnalysis(DelPrintIntegrityInfo handler, IReadOnlyList<MsilInstruction> instructions)
        {
            MethodTranspiler.IntegrityAnalysis(handler, instructions);
        }

#pragma warning disable 649
        [ReflectedStaticMethod(Type = typeof(RuntimeHelpers), Name = "_CompileMethod", OverrideTypeNames = new[] {"System.IRuntimeMethodInfo"})]
        private static Action<object> _compileDynamicMethod;

        [ReflectedMethod(Name = "GetMethodInfo")]
        private static Func<RuntimeMethodHandle, object> _getMethodInfo;

        [ReflectedMethod(Name = "GetMethodDescriptor")]
        private static Func<DynamicMethod, RuntimeMethodHandle> _getMethodHandle;
#pragma warning restore 649
        /// <summary>
        /// Forces the given dynamic method to be compiled
        /// </summary>
        /// <param name="method"></param>
        public static void Compile(DynamicMethod method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));

            // DynamicMethod derives from MethodInfo, so this is safe.
            var mi = (MethodInfo)method;

            // Build a delegate type matching the signature (params..., return)
            var paramTypes = mi.GetParameters().Select(p => p.ParameterType).ToArray();
            var returnType = mi.ReturnType;

            var delegateType = CreateDelegateType(paramTypes, returnType);

            // Create and "prepare" the delegate; this finalizes IL and JITs it
            var del = method.CreateDelegate(delegateType);
            RuntimeHelpers.PrepareDelegate(del);
            // No invocation necessary here; JIT preparation is enough.
        }

        private static Type CreateDelegateType(Type[] parameterTypes, Type returnType)
        {
            // Expression.GetDelegateType supports both void and non-void return.
            // For void, the return type must be omitted, but Expression.GetDelegateType
            // requires including return type; for void, use typeof(void).
            var typeArgs = (returnType == typeof(void))
                ? parameterTypes.Concat(new[] { typeof(void) }).ToArray()
                : parameterTypes.Concat(new[] { returnType }).ToArray();

            return Expression.GetDelegateType(typeArgs);
        }
    }
}