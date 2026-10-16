using AiGateway.App;
using Xunit;

namespace AiGateway.Tests;

// 回归：复制必须快速返回、诚实报告。
// 不断言「剪贴板里现在是这份文本」——剪贴板是系统全局共享资源，截图工具/剪贴板管理器
// 会在任何时刻改写它（实测整套测试跑时被改写过、单跑 5/5 全过），那种断言必然随机失败。
// 写入成功由 SetClipboardData 的返回值决定；读回验证由本地探针做，不进测试套件。
public class ClipboardTextTests
{
    [Fact]
    public void Copy_never_blocks_the_caller()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = ClipboardText.TryCopy("aigateway-clipboard-probe-" + Guid.NewGuid().ToString("N"));
        clock.Stop();

        // 这条是本类存在的理由：曾出现点复制就卡顿（OLE 的 flush 会阻塞约 1 秒）。
        // 被占用时应快速失败、空闲时应快速成功，唯一不接受的是阻塞。
        Assert.True(clock.ElapsedMilliseconds < 500, $"复制必须快速返回，实际耗时 {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Copy_of_empty_text_is_refused()
    {
        Assert.False(ClipboardText.TryCopy(string.Empty));
        Assert.False(ClipboardText.TryCopy(null!));
    }
}