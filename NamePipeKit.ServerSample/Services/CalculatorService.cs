using NamePipeKit.Abstractions.Attributes;

namespace NamePipeKit.ServerSample.Services
{
    /// <summary>
    /// 计算器服务。
    /// <para>
    /// 演示：**想给客户端增加一个新能力，只要新建这个类，
    /// 打上 [PipeService] 和方法级 [PipeMethod]，不用改任何框架代码**。
    /// 下次服务端启动就会自动注册。
    /// </para>
    /// </summary>
    [PipeService]
    public sealed class CalculatorService
    {
        /// <summary>整数相加。</summary>
        [PipeMethod("calculator.add", Description = "整数相加")]
        public int Add(int a, int b)
        {
            return a + b;
        }

        /// <summary>浮点相除。</summary>
        [PipeMethod("calculator.div", Description = "浮点相除")]
        public double Divide(double a, double b)
        {
            if (b == 0)
                throw new System.DivideByZeroException("除数不能为 0");
            return a / b;
        }
    }
}