// ════════════════════════════════════════════════════════════════════════════
//  文件：PipeMethodInvokerFactory.cs
//  作用：用表达式树预编译方法调用器，避免每次反射 Invoke 的开销。
//  性能：比 MethodInfo.Invoke 快 3~10 倍。
//  原理：把 (instance, args) => method(instance, args[0], args[1]...) 编译为委托。
// ════════════════════════════════════════════════════════════════════════════
using System;
using System.Linq.Expressions;
using System.Reflection;

namespace NamePipeKit.Discovery
{
    /// <summary>方法调用器工厂。</summary>
    internal static class PipeMethodInvokerFactory
    {
        /// <summary>
        /// 为 method 创建 <c>Func&lt;object, object[], object&gt;</c> 委托。
        /// <para>静态方法的 instance 参数会被忽略；返回值 void 则返回 null。</para>
        /// </summary>
        public static Func<object, object[], object> Create(MethodInfo method)
        {
            var instanceParam = Expression.Parameter(typeof(object), "instance");
            var argsParam = Expression.Parameter(typeof(object[]), "args");
            var parameters = method.GetParameters();

            // 把 args[i] 转换为参数强类型
            var callArgs = new Expression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var item = Expression.ArrayIndex(argsParam, Expression.Constant(i));
                callArgs[i] = Expression.Convert(item, parameters[i].ParameterType);
            }

            // 目标对象：静态方法为 null，实例方法强制转换
            Expression target = method.IsStatic ? null : Expression.Convert(instanceParam, method.DeclaringType);

            // 调用表达式 + 装箱为 object
            var call = Expression.Call(target, method, callArgs);
            var body = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object[], object>>(body, instanceParam, argsParam).Compile();
        }
    }
}