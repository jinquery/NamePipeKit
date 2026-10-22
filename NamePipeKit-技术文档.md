# NamePipeKit 技术文档

> 一个基于 Windows 命名管道的进程间通信框架
> - 采用**开闭原则**设计，业务扩展零侵入
> - 支持**同步请求/响应**、**服务端主动推送**、**流式响应**三种通信模式
> - 自定义二进制协议（魔数 + 版本 + CRC32）
> - **C# 7.3 兼容**，零 NuGet 依赖

---

## 目录

1. [项目概述](#1-项目概述)
2. [整体架构](#2-整体架构)
3. [协议规范](#3-协议规范)
4. [核心组件](#4-核心组件)
5. [线程模型](#5-线程模型)
6. [使用指南](#6-使用指南)
7. [扩展指南](#7-扩展指南)
8. [性能分析](#8-性能分析)
9. [诊断工具](#9-诊断工具)
10. [常见问题](#10-常见问题)
11. [设计决策记录](#11-设计决策记录)

---

## 1. 项目概述

### 1.1 定位

NamePipeKit 是一个轻量级、可扩展、高性能的**本机进程间通信框架**，专为 Windows 命名管道设计。

**适用场景**：

| 场景 | 说明 |
|---|---|
| 桌面应用的插件通信 | 主进程 ↔ 插件进程 |
| 前后端分离的本地服务 | UI 进程 ↔ 后台服务进程 |
| 多进程协作 | 一个服务端，多个客户端进程 |
| 需要事件推送的场景 | 日志订阅、实时通知 |

**不适用场景**：

| 场景 | 推荐替代 |
|---|---|
| 跨机器通信 | gRPC / TCP / SignalR |
| 单进程内部通信 | 直接方法调用 |
| 极高频消息（>10万/秒） | 共享内存 / 消息队列 |

### 1.2 核心特性

| 特性 | 说明 |
|---|---|
| **开闭原则** | 新增业务/请求类型/中间件/协议/传输层全部"加代码不改代码" |
| **三种通信模式** | 同步请求、服务端推送、流式响应 |
| **自定义协议** | 魔数 + 版本号 + CRC32，支持协议演进 |
| **零 NuGet 依赖** | C# 7.3 兼容，.NET Framework 4.7.2+ / .NET 6+ 均可 |
| **专用线程模型** | 客户端/服务端均使用专用线程 + 同步 IO，不抢 ThreadPool |
| **可观测性** | TraceId 贯穿、停顿探测、耗时统计 |

---

## 2. 整体架构

### 2.1 分层视图

```
┌─────────────────────────────────────────────────────────────────┐
│                         业务层                                   │
│   [PipeService] + [PipeMethod]  ·  IPipeRequestHandler           │
├─────────────────────────────────────────────────────────────────┤
│                         编排层                                   │
│   PipeServer  ·  PipeServerBuilder  ·  HandlerRegistry           │
│   Pipeline (Middleware)  ·  EventBus                             │
├─────────────────────────────────────────────────────────────────┤
│                         协议层                                   │
│   IFrameCodec  ·  FrameCodecV1  ·  PipeFrame                     │
│   PipeRequestHeader / PipeResponseHeader                         │
├─────────────────────────────────────────────────────────────────┤
│                         序列化层                                 │
│   IPipeSerializer  ·  JsonPipeSerializer                         │
├─────────────────────────────────────────────────────────────────┤
│                         传输层                                   │
│   IPipeServerTransport  ·  NamedPipeServerTransport              │
│   IPipeClientTransport  ·  NamedPipeClientTransport              │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 项目结构

```
NamePipeKit/
├── NamePipeKit.Abstractions/          # 抽象层（无实现依赖）
│   ├── Attributes/                     # [PipeService] / [PipeMethod]
│   ├── Protocol/                       # 协议数据模型
│   ├── Serialization/                  # 序列化接口
│   ├── Transport/                      # 传输层接口
│   ├── Handling/                       # Handler / Middleware 接口
│   └── Logging/                        # 日志接口
│
├── NamePipeKit/                        # 默认实现
│   ├── Protocol/                       # FrameCodecV1
│   ├── Serialization/                  # JsonPipeSerializer
│   ├── Transport/                      # NamedPipeServer/ClientTransport
│   ├── Discovery/                      # 方法扫描与调用器预编译
│   ├── Handling/                       # CallMethod/Message/Subscribe Handler
│   ├── Middleware/                     # Exception/Logging/Timing
│   ├── Server/                         # PipeServer / PipeServerBuilder
│   ├── Client/                         # PipeClient
│   └── Logging/                        # ConsolePipeLogger
│
├── NamePipeKit.ServerSample/           # 服务端示例
├── NamePipeKit.ClientSample/           # 客户端示例
└── NamePipeKit.StressTest/             # 压测工具
```

### 2.3 关键接口关系

```
IPipeServerTransport ────► IPipeConnection ──► IFrameCodec
       │                         │
       │                         └──► IPipeSerializer
       │
       ▼
   PipeServer ──► HandlerRegistry ──► IPipeRequestHandler
       │                                       │
       │                                       ├── CallMethodHandler
       │                                       ├── MessageHandler
       │                                       ├── SubscribeHandler
       │                                       └── 自定义Handler...
       │
       ▼
   IPipeMiddleware[] (Exception → Logging → Timing → 业务)
```

---

## 3. 协议规范

### 3.1 二进制帧格式

```
偏移    长度   字段               说明
─────────────────────────────────────────────────────────────
0       2      Magic              固定 0x50 0x50 ("PP")
2       1      Version            协议版本，当前 1
3       1      Flags              预留标志位
4       1      MessageType        帧类型（1-9）
5       8      SequenceId         请求/响应配对号（大端）
13      4      HeaderLength       Header 字节数（大端）
17      4      BodyLength         Body 字节数（大端）
21      N      Header             UTF-8 JSON
21+N    M      Body               UTF-8 JSON 或原始二进制
21+N+M  4      CRC32              对 Header+Body 计算（大端）

固定开销 = 21 + 4 = 25 字节
```

### 3.2 MessageType 枚举

| 值 | 名称 | 方向 | 说明 |
|---|---|---|---|
| 1 | Request | C → S | 业务请求 |
| 2 | Response | S → C | 业务响应 |
| 3 | Heartbeat | 双向 | 心跳保活 |
| 4 | Handshake | 双向 | 协议协商（预留） |
| 5 | Error | S → C | 协议级错误 |
| 6 | Cancel | C → S | 取消请求（预留） |
| **7** | **ServerPush** | **S → C** | **服务端主动推送** |
| **8** | **ResponseChunk** | **S → C** | **流式响应数据块** |
| **9** | **ResponseEnd** | **S → C** | **流式响应结束** |

### 3.3 ContentType 常量

| ContentType | Handler | 用途 |
|---|---|---|
| `CALLMETHOD` | CallMethodHandler | 调用 `[PipeMethod]` 业务方法 |
| `MESSAGE` | MessageHandler | 发送纯文本消息 |
| `SUBSCRIBE` | SubscribeHandler | 订阅 topic |
| `UNSUBSCRIBE` | UnsubscribeHandler | 取消订阅 |
| `STREAM` | StreamHandler（用户实现） | 流式响应 |
| `EVENT` | — | 仅作为 ServerPush 帧的标识 |

### 3.4 状态码

| 码 | 含义 |
|---|---|
| `0000` | 成功 |
| `1000` | 请求格式错误 |
| `1001` | 请求数据为空 |
| `1002` | 不支持的 ContentType |
| `2000` | 方法不存在 |
| `3000` | 参数绑定失败 |
| `3001` | 方法调用失败 |
| `5000` | 服务端内部错误 |
| `5001` | 传输层错误 |

### 3.5 三类通信模式流程

#### 模式一：同步请求/响应

```
Client                                Server
  │                                     │
  ├──[Request]───────────────►│
  │                           ├─ 处理
  │                           ├─
  ├─◄──────────[Response]─────┤
  │                                     │
```

#### 模式二：服务端主动推送

```
Client                                Server
  │                                     │
  ├──[Request: SUBSCRIBE]────►│
  ├─◄──────[Response: OK]─────┤
  │                                     │
  │                           ├─ 业务触发
  ├─◄────[ServerPush]─────────┤
  ├─◄────[ServerPush]─────────┤
  │                                     │
  ├──[Request: UNSUBSCRIBE]──►│
  ├─◄──────[Response: OK]─────┤
```

#### 模式三：流式响应

```
Client                                Server
  │                                     │
  ├──[Request: STREAM]───────►│
  ├─◄──────[Response: 头]─────┤
  ├─◄──[ResponseChunk 1]──────┤
  ├─◄──[ResponseChunk 2]──────┤
  ├─◄──[ResponseChunk 3]──────┤
  ├─◄──[ResponseChunk N]──────┤
  ├─◄──[ResponseEnd]──────────┤
  │                                     │
```

---

## 4. 核心组件

### 4.1 PipeServerBuilder（构建器）

链式构建，所有能力都通过注册注入。

```csharp
using (var server = new PipeServerBuilder("MyPipe")
    .UseLogger(new ConsolePipeLogger())        // 日志
    .UseMaxInstances(100)                       // 最大连接数
    .UseFrameCodec(new FrameCodecV1(logger))    // 协议
    .ScanAssemblies(typeof(Program).Assembly)   // 扫描 [PipeMethod]
    .AddHandler(new SubscribeHandler(bus))      // 自定义 Handler
    .AddMiddleware(new AuthMiddleware())        // 中间件
    .OnMessageReceived((s, h, d) => { })        // 消息订阅
    .Build())
{
    server.StartAsync();
    Console.ReadLine();
    server.Stop();
}
```

### 4.2 HandlerRegistry（处理器路由）

**开闭原则核心**：ContentType → Handler 的路由表。

```csharp
public sealed class HandlerRegistry
{
    private ConcurrentDictionary<string, IPipeRequestHandler> _handlers;

    public HandlerRegistry Register(IPipeRequestHandler handler);
    public IPipeRequestHandler Resolve(string contentType);
}
```

**新增请求类型**：实现 `IPipeRequestHandler` + `AddHandler` 注册。**现有代码一行不改**。

### 4.3 PipeMethodRegistry（业务方法注册表）

扫描所有 `[PipeService]` 类上带 `[PipeMethod]` 的方法，用**表达式树预编译**调用器。

```csharp
[PipeService]
public class OrderService
{
    [PipeMethod("order.create")]
    public Task<string> CreateOrderAsync(string userId, decimal amount) { ... }
}
```

**性能**：`Func<object, object[], object>` 委托调用，比 `MethodInfo.Invoke` 快 3~10 倍。

### 4.4 EventBus（事件总线）

```csharp
public sealed class EventBus
{
    public void Subscribe(string topic, IPipeConnection conn);
    public void Unsubscribe(string topic, IPipeConnection conn);
    public void Publish(string topic, object payload);
}
```

- `Subscribe` / `Unsubscribe`：由 SubscribeHandler 自动调用
- `Publish`：业务侧主动调用，向所有订阅者推 `ServerPush` 帧

### 4.5 中间件管道

```
ExceptionMiddleware          ← 兜底所有未处理异常
    │
LoggingMiddleware            ← 打印请求/响应概要
    │
TimingMiddleware             ← 计算处理耗时
    │
AuthMiddleware（自定义）      ← 鉴权
    │
RateLimitMiddleware（自定义） ← 限流
    │
业务 Handler                 ← 最终处理
```

**新增中间件**：实现 `IPipeMiddleware` + `AddMiddleware` 注册。**现有代码一行不改**。

---

## 5. 线程模型

### 5.1 服务端

```
┌──────────────────────────────────────────────────────────────┐
│  主线程                                                       │
│  ListenAsync() → 启动 32 个 Accept 专用线程 → 主线程挂起      │
├──────────────────────────────────────────────────────────────┤
│  Accept 专用线程 × 32（DefaultWaitSlotCap）                   │
│  while (true):                                               │
│    stream = CreateServerStream()                             │
│    stream.WaitForConnection()      ← 同步阻塞                │
│    new Thread(HandleConnection).Start()                       │
├──────────────────────────────────────────────────────────────┤
│  连接专用线程 × N（每个连接一个）                              │
│  HandleConnection:                                            │
│    while (true):                                              │
│      frame = codec.Read(stream)    ← 同步读                  │
│      response = pipeline(frame)                               │
│      codec.Write(stream, response) ← 同步写                  │
└──────────────────────────────────────────────────────────────┘
```

**关键设计**：

- **Accept 不用 `WaitForConnectionAsync`**：它在 Windows 上是伪异步，会抢 ThreadPool
- **每连接一个专用线程**：整个"读-处理-写"在专用线程同步执行
- **零 ThreadPool 占用**

### 5.2 客户端

```
┌──────────────────────────────────────────────────────────────┐
│  Worker 专用线程 × 1（每个 PipeClient 一个）                  │
│  while (true):                                                │
│    item = queue.Take()             ← 从队列取请求            │
│    codec.Write(stream, item.Frame) ← 同步写                  │
│    while (true):                                              │
│      frame = codec.Read(stream)    ← 同步读                  │
│      if Response:                                             │
│        item.Tcs.TrySetResult(frame)                           │
│        break                                                  │
│      if ResponseChunk:                                        │
│        item.OnChunk(frame.Body)                               │
│      if ResponseEnd:                                          │
│        item.Tcs.TrySetResult(null)                            │
│        break                                                  │
│      if ServerPush:                                           │
│        PushReceived(frame.Body)                               │
│      if Heartbeat:                                            │
│        continue                                               │
└──────────────────────────────────────────────────────────────┘
```

**关键设计**：

- **单线程模型**：一个 Worker 处理所有请求（普通/流式/推送共用）
- **TCS 用 `RunContinuationsAsynchronously`**：避免 Worker 被调用方 continuation 占满
- **流式请求不额外占线程**：Worker 循环里多读几帧即可

### 5.3 线程数估算

| 并发数 | 服务端 Accept | 服务端连接 | 客户端 Worker | 合计 |
|---|---|---|---|---|
| 10 | 32（满） | 10 | 10 | **52** |
| 50 | 32 | 50 | 50 | **132** |
| 100 | 32 | 100 | 100 | **232** |
| 200 | 32 | 200 | 200 | **432** |

**线程数 = 上下文切换开销**。50 并发时 132 线程，在 4~8 核机器上已经有明显竞争。

---

## 6. 使用指南

### 6.1 服务端最小示例

```csharp
// 1. 定义业务服务
[PipeService]
public class CalculatorService
{
    [PipeMethod("calc.add")]
    public int Add(int a, int b) { return a + b; }
}

// 2. 启动服务端
using (var server = new PipeServerBuilder("MyPipe")
    .UseLogger(new ConsolePipeLogger())
    .UseMaxInstances(100)
    .ScanAssemblies(typeof(Program).Assembly)
    .Build())
{
    server.StartAsync();
    Console.ReadLine();
    server.Stop();
}
```

### 6.2 客户端最小示例

```csharp
using (var client = new PipeClient("MyPipe", ".", "my-client"))
{
    var result = await client.InvokeAsync<int>("calc.add", 100, 200);
    Console.WriteLine(result);  // 300
}
```

### 6.3 订阅推送

**服务端**：

```csharp
var eventBus = new EventBus();
using (var server = new PipeServerBuilder("MyPipe")
    .AddHandler(new SubscribeHandler(eventBus))
    .AddHandler(new UnsubscribeHandler(eventBus))
    .Build())
{
    server.StartAsync();
    
    // 业务侧随时推
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(1000);
            eventBus.Publish("heartbeat", new { time = DateTime.Now });
        }
    });
    
    Console.ReadLine();
}
```

**客户端**：

```csharp
using (var client = new PipeClient("MyPipe"))
{
    client.PushReceived += body =>
    {
        Console.WriteLine("推送: " + Encoding.UTF8.GetString(body));
    };
    await client.SubscribeAsync("heartbeat");
    
    await Task.Delay(10000);
    await client.UnsubscribeAsync("heartbeat");
}
```

### 6.4 流式响应

**服务端**（Handler）：

```csharp
public class StreamHandler : IPipeRequestHandler
{
    public string ContentType => PipeContentTypes.Stream;

    public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
    {
        return Task.FromResult(PipeResponse.Stream(
            headerData: new { count = 20 },
            streamFactory: async (push, complete) =>
            {
                try
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(100);
                        push(Encoding.UTF8.GetBytes("chunk " + i));
                    }
                }
                finally { complete(); }
            }));
    }
}
```

**客户端**（C# 7.3 兼容）：

```csharp
await client.StreamWithCallbackAsync(
    "stream.counter",
    new object[] { 20 },
    chunk => Console.WriteLine(Encoding.UTF8.GetString(chunk)),
    CancellationToken.None);
```

---

## 7. 扩展指南

### 7.1 新增业务方法

**只需一个文件**：

```csharp
[PipeService]
public class NewService
{
    [PipeMethod("new.method")]
    public string DoWork(string input) { return "done"; }
}
```

框架自动扫描注册，**其它代码零改动**。

### 7.2 新增请求类型

**需要两步**：

1. 实现 `IPipeRequestHandler`
2. `AddHandler()` 注册

```csharp
public class BatchHandler : IPipeRequestHandler
{
    public string ContentType => "BATCH";
    
    public Task<PipeResponse> HandleAsync(PipeRequest request, PipeContext ctx, CancellationToken ct)
    {
        // 自定义处理逻辑
        return Task.FromResult(PipeResponse.Ok());
    }
}

// 注册
builder.AddHandler(new BatchHandler());
```

### 7.3 新增中间件

```csharp
public class AuthMiddleware : IPipeMiddleware
{
    public async Task<PipeResponse> InvokeAsync(PipeContext ctx, PipeRequestDelegate next)
    {
        var token = ctx.Request.Metadata?["token"];
        if (token != "secret")
            return PipeResponse.Fail("1003", "鉴权失败");
        return await next(ctx);
    }
}

builder.AddMiddleware(new AuthMiddleware());
```

### 7.4 替换序列化器

```csharp
public class MessagePackPipeSerializer : IPipeSerializer
{
    public byte[] Serialize(object value, Type type)
        => MessagePackSerializer.Serialize(type, value);
    
    public object Deserialize(byte[] data, Type type)
        => MessagePackSerializer.Deserialize(type, data);
    
    public T Deserialize<T>(byte[] data)
        => MessagePackSerializer.Deserialize<T>(data);
}

// 两端都用同一个
builder.UseSerializer(new MessagePackPipeSerializer());
new PipeClient("MyPipe", serializer: new MessagePackPipeSerializer());
```

### 7.5 替换传输层

实现 `IPipeServerTransport` / `IPipeClientTransport`，然后用 `UseTransport` 注入。

**关键约定**：新传输层只需保证 `ReadFrameAsync` / `WriteFrameAsync` 正确。

### 7.6 协议版本升级

1. 实现 `IFrameCodec`（如 `FrameCodecV2`）
2. `UseFrameCodec(new FrameCodecV2(logger))`
3. 客户端也传入 `codec: new FrameCodecV2(...)`

或在运行时通过 `IPipeConnection.SwitchCodec()` 切换（配合 Handshake 协商）。

### 7.7 扩展点速查

| 想做的事 | 需要写什么 | 需要改框架代码？ |
|---|---|---|
| 新增业务方法 | `[PipeMethod]` 方法 | ❌ |
| 新增请求类型 | `IPipeRequestHandler` + `AddHandler` | ❌ |
| 新增中间件 | `IPipeMiddleware` + `AddMiddleware` | ❌ |
| 新增推送 topic | `EventBus.Publish` | ❌ |
| 新增流式方法 | Handler 返回 `PipeResponse.Stream(...)` | ❌ |
| 替换序列化 | `IPipeSerializer` + `UseSerializer` | ❌ |
| 替换协议 | `IFrameCodec` + `UseFrameCodec` | ❌ |
| 替换传输层 | `IPipeTransport` + `UseTransport` | ❌ |
| 接入 DI | `UseServiceFactory(sp.GetService)` | ❌ |

---

## 8. 性能分析

### 8.1 裸测基准（同步 API · 单连接）

```
请求数：10,000
耗时：0.31 秒
QPS：31,825
单次 RTT：0.031 ms
```

**结论**：Windows 命名管道同步 API 单次往返能力**极强**（0.03ms）。

### 8.2 框架压测（50 并发 · 内嵌服务端）

| 指标 | 值 | 说明 |
|---|---|---|
| QPS | **631~937** | 波动范围 |
| Min RTT | **0.29~2.3ms** | 接近裸测，说明单次处理能力保留 |
| P50 RTT | **34~45ms** | 排队+调度延迟 |
| P99 RTT | **136~573ms** | 长尾（GC / 调度 / 系统抢占） |
| Max RTT | **503ms~28s** | 偶发全局暂停 |

### 8.3 QPS 的物理上限分析

**937 QPS × 50 并发 = 每请求 53ms**。

53ms 的来源（Windows 15.6ms 时间片 + 132 线程）：

```
4 次线程唤醒 × 7.8ms（平均时间片等待） = 31ms
+ 处理+序列化+CRC                     = 22ms
= 53ms
```

**这不是代码 bug，是 Windows 调度 + 132 线程竞争的物理极限**。

### 8.4 优化路径（收益递减）

| 优化手段 | 预期收益 | 代价 |
|---|---|---|
| **加大并发到 sweet spot** | **1.5~2 倍** | 零改动（只改配置） |
| Server GC + 排除杀毒 | 10~30% | 改 csproj + 系统设置 |
| 批处理（每帧 N 请求） | 5~10 倍 | 改协议 + 客户端模型 |
| 换 TCP（IOCP） | 50~100 倍 | 改传输层（3~5 天） |
| 换共享内存 | 1000+ 倍 | 只支持本机 |

### 8.5 QPS 与业务的匹配

**QPS 631 / P50 45ms 在真实业务中完全够用**：

- 业务方法 5ms 处理时间 → 631 QPS = 每秒 315ms CPU 时间（3 核即可承载）
- 业务方法 50ms 处理时间 → 631 QPS = 每秒 31.5s CPU 时间（16 核满载）

**绝大多数桌面应用的 IPC 场景不需要 > 1000 QPS**。

---

## 9. 诊断工具

### 9.1 停顿探测器

`StressRunner` 内置，压测期间每 50ms 采样，检测 >300ms 的全局暂停。

**输出示例**：

```
╔══════════════════════════════════════════════════════════════╗
║                   停顿诊断报告                                ║
╚══════════════════════════════════════════════════════════════╝
  压测总时长      ：73.73 秒
  GC 总次数       ：Gen0=1234 Gen1=56 Gen2=3
  停顿次数(>300ms)：4

  最长单次停顿    ：26307 ms
  累计停顿时长    ：27301 ms （占 37.0% 总时长）
  其中 Gen2 增长  ：2 次（说明是 GC 引起的）

  ──────────  停顿明细（前 20 条） ──────────
  [10:23:15.123]  暂停     433ms  GC=456/12/1  TP= 87  done=12,456
  [10:23:22.456]  暂停   26307ms  GC=789/23/2  TP= 43  done=18,901
  ...
```

**解读**：

| 现象 | 根因 | 修法 |
|---|---|---|
| Gen2 计数在暂停时增长 | Gen2 GC | 加 `ServerGarbageCollection=true` |
| Gen2 没变但暂停很长 | 系统挂起 | 排查杀毒/更新/内存 |
| TP 线程数很高 | ThreadPool 饥饿 | 用外部服务端模式 |

### 9.2 首次失败打印

压测时第一个失败会立即打印完整堆栈，避免"跑完看汇总"才发现错误。

### 9.3 客户端/服务端日志

```csharp
// 服务端
.UseLogger(new ConsolePipeLogger())

// 客户端
new PipeClient("MyPipe", logger: new ConsolePipeLogger())
```

---

## 10. 常见问题

### Q1：QPS 为什么上不去 1000？

**A**：Windows 命名管道 + 132 线程的物理极限。加大并发试试，如果 QPS 线性增长，说明还是时间片瓶颈；如果 QPS 饱和，说明 CPU 核数到顶了。

### Q2：Debug 模式和 Release 差别大吗？

**A**：约 10~20%。Debug 下 JIT 不内联、边界检查更严。**压测必须用 Release**。

### Q3：能跨机器通信吗？

**A**：可以，但要 `AllowNetwork(true)`，且需要在防火墙放行 SMB（445 端口）。**跨机器用 TCP 更合适**。

### Q4：客户端断开后服务端会怎样？

**A**：`ReadFrameAsync` 抛 `EndOfStreamException`，连接线程 `break`，`connection.Dispose()` 清理资源，不影响其他连接。

### Q5：消息顺序能保证吗？

**A**：**同一连接内**严格顺序（单线程读），**不同连接间**无顺序保证。

### Q6：断线重连吗？

**A**：客户端 Worker 遇到异常会 `InvalidateStream`，**下一次请求自动重连**（最多 3 次）。

### Q7：服务端推送会丢吗？

**A**：`EventBus.Publish` 时如果连接已断，该连接会从订阅表移除，消息丢弃。**不保证可靠投递**，如果需要请加 ACK 机制。

### Q8：流式响应和普通请求能并发吗？

**A**：**同一连接内串行**（Worker 是单线程）。多连接可以并发。

### Q9：压力测试为什么有时 P99 突然几百毫秒？

**A**：90% 是 GC Gen2 停顿或系统抢占，用停顿探测器确认。

### Q10：C# 7.3 能用吗？

**A**：能。核心框架、压测项目都严格 7.3 兼容。`await foreach` / `IAsyncEnumerable` / `Channel<T>` 全部已移除。

---

## 11. 设计决策记录

### 11.1 为什么用同步 IO 而不是异步？

**决策**：全部用 `Stream.Read` / `Stream.Write` 同步 API。

**理由**：

1. `NamedPipeStream.ReadAsync` 在 Windows 上是**伪异步**——底层同步阻塞包装，会抢 ThreadPool 线程
2. 高并发下会抢空 ThreadPool，导致 TCS continuation 无法调度，形成饥饿
3. 用专用线程跑同步 IO，**ThreadPool 完全空闲**，反而更高效

**实测**：删掉 `FlushAsync` 后 QPS 从 246 提升到 891（3.6 倍）。

### 11.2 为什么绝不调用 Flush？

**决策**：`FrameCodec.Write` 写完后**不调用 `Flush`**。

**理由**：

- `PipeStream.Flush` 在命名管道上等价于 `FlushFileBuffers`
- 语义是"**阻塞等待对端把数据读走**"
- 每写一帧就 Flush 一次 = 每帧多等 30~40ms

**实测**：删掉 Flush 是 QPS 提升的头号因素。

### 11.3 为什么服务端用专用线程而不是 Async？

**决策**：每个连接一个专用 `Thread`，`HandleConnectionAsync` 用 `GetAwaiter().GetResult()` 同步等待。

**理由**：

- 配合 `NamedPipeConnection.ReadFrameAsync` 返回 `Task.FromResult(同步读)`，`await` 同步完成
- 整个连接生命周期在专用线程同步执行，**零 ThreadPool 占用**
- ThreadPool 只处理 TCS continuation，压力极小

### 11.4 为什么客户端用单 Worker 而不是双线程？

**决策**：Writer + Reader 合并为单 Worker 线程。

**理由**：

- 双线程会增加一倍线程数（50 客户端 = 100 线程），上下文切换翻倍
- **实测双线程 QPS 从 937 掉到 619**
- 单线程 Worker 循环里，流式响应通过"多读几帧"实现，**不需要分离线程**

### 11.5 为什么 TCS 用 RunContinuationsAsynchronously？

**决策**：`TaskCompletionSource` 加 `TaskCreationOptions.RunContinuationsAsynchronously`。

**理由**：

- 不加：`TrySetResult` 时调用方 continuation **内联执行在 Worker 线程上**
- 压测场景下，continuation 是压测循环（记录延迟 + 提交下一个请求）
- 内联会**占满 Worker 线程**，Worker 无法处理下一个请求，形成"自己等自己"

**实测**：内联时 QPS 从 937 掉到 300。

### 11.6 为什么协议头是 21 字节？

**决策**：`Magic(2) + Version(1) + Flags(1) + MessageType(1) + SequenceId(8) + HeaderLen(4) + BodyLen(4) = 21`。

**理由**：

- 2 字节魔数足以识别协议（"PP"）
- 8 字节 SequenceId 支持 2^63 个请求
- 4 字节长度字段支持 4GB 帧（实际 16MB 上限）
- **25 字节额外开销，在 100 字节以上的业务载荷面前可以忽略**

### 11.7 为什么用字符串 ContentType 而不是枚举？

**决策**：`PipeContentTypes` 全是 `const string`。

**理由**：

- **开闭原则**：新增 ContentType 只需新增字符串常量 + 新 Handler
- **不需要改协议层**：字符串本来就能传任何值
- **向后兼容**：旧服务端收到未知 ContentType 返回"不支持"错误，不崩溃

### 11.8 为什么用表达式树预编译方法调用器？

**决策**：`PipeMethodInvokerFactory.Create` 用 `Expression.Lambda` 编译成委托。

**理由**：

- `MethodInfo.Invoke` 每次都要做参数装箱和反射查找
- 表达式树编译一次，之后是普通委托调用
- **实测 3~10 倍性能提升**

### 11.9 为什么不依赖 `System.Threading.Channels`？

**决策**：流式响应用"事件回调"而不是 `Channel<T>`。

**理由**：

1. `System.Threading.Channels` 在 .NET Framework 需要 NuGet 包
2. `ChannelReader.ReadAllAsync` 配 `await foreach` 需要 C# 8.0
3. **事件回调 + `TaskCompletionSource` 完全等价**，零依赖，7.3 兼容

### 11.10 为什么加停顿探测器？

**决策**：`StressRunner` 内置每 50ms 采样的停顿检测。

**理由**：

- P99 / Max 异常高时（如 26 秒）**无法从单次请求耗时判断是"处理慢"还是"进程被冻结"**
- 停顿探测器能捕获"整个进程暂停"的事件，并关联 GC / ThreadPool 状态
- **把"猜测"变成"数据驱动"**

---

## 附录 A：完整测试命令清单

```bash
# 编译
dotnet build NamePipeKit.sln -c Release

# 服务端（独立进程）
cd src/NamePipeKit.ServerSample
dotnet run -c Release

# 客户端（另开终端）
cd src/NamePipeKit.ClientSample
dotnet run -c Release

# 压测：内嵌模式（默认）
cd src/NamePipeKit.StressTest
dotnet run -c Release

# 压测：外部模式（先启动 ServerSample）
dotnet run -c Release -- --server=. --pipe=NamePipeKit.Sample.v1 --method=system.ping --args=hello --concurrency=50 --requests=1000

# 压测：流式模式
dotnet run -c Release -- --auto-server=true --mode=stream --concurrency=20 --requests=100

# 压测：按持续时间
dotnet run -c Release -- --auto-server=true --concurrency=100 --duration=30
```

## 附录 B：关键性能数字

| 场景 | QPS | RTT P50 |
|---|---|---|
| 裸测（同步 · 单连接） | 31,825 | 0.031ms |
| 框架 · 10 并发 | ~250~400 | 25~40ms |
| 框架 · 50 并发 | **631~937** | 34~53ms |
| 框架 · 100 并发 | 待测（预期 1000~1500） | — |

## 附录 C：已知限制

| 限制 | 说明 | 变通方案 |
|---|---|---|
| 单机上限 | 命名管道只在本机 | 跨机用 TCP 传输层 |
| 单连接串行 | 客户端 Worker 单线程 | 多创建几个 PipeClient |
| 无 ACK 机制 | ServerPush 不保证可靠 | 业务侧自行实现 ACK |
| 无流控 | 客户端可无限推请求 | 队列有 100000 上限 |
| 无加密 | 明文帧 | 换 TLS 传输层或应用层加密 |

---

**文档版本**：1.0
**最后更新**：2024
**维护者**：NamePipeKit Team