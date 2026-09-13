using System.Text.Json;

namespace UstcCourseAssistant;

public interface ICourseSelectionAdapter
{
    Task<IReadOnlySet<string>> GetSelectedCourseCodesAsync(CancellationToken cancellationToken);
    Task<CourseProbe?> ProbeAsync(string courseCode, CancellationToken cancellationToken);
    Task<SelectionResult> DropAsync(string courseCode, CancellationToken cancellationToken);
    Task<SelectionResult> SelectAsync(string courseCode, CancellationToken cancellationToken);
}

public sealed record SelectionResult(bool Success, string Message);
public sealed record CourseProbe(string? Name, int? RemainingSeats, bool AlreadySelected);

// 所有页面操作只发生在用户已认证的内置网页中；不读取密码、验证码或 Cookie。
public sealed class WebViewCourseSelectionAdapter : ICourseSelectionAdapter
{
    public async Task<IReadOnlySet<string>> GetSelectedCourseCodesAsync(CancellationToken token)
    {
        EnsureSession();
        // 只读取“已选所有课程”，绝不在此页面点击任何课程操作。
        await ScriptAsync("(() => { const b=[...document.querySelectorAll('button,a')].find(x=>x.innerText.includes('已选所有课程')); if(b)b.click(); return {ok:!!b}; })()");
        await Task.Delay(800, token);
        var state = await ScriptAsync("(() => ({text:document.body.innerText}))()");
        var text = state.GetProperty("text").GetString() ?? "";
        return System.Text.RegularExpressions.Regex.Matches(text, @"\b[0-9A-Z]{4,}\.[0-9A-Z]+\b")
            .Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CourseProbe?> ProbeAsync(string courseCode, CancellationToken token)
    {
        await SearchAsync(courseCode, token);
        try
        {
            var row = await ReadCourseRowAsync(courseCode);
            await Task.Delay(900, token);
            var refreshed = await ReadCourseRowAsync(courseCode);
            if (refreshed.TryGetProperty("found", out var refreshedFound) && refreshedFound.GetBoolean()) row = refreshed;
            if (!row.TryGetProperty("found", out var found) || !found.GetBoolean()) return null;
            int? seats = row.TryGetProperty("seats", out var seatValue) && seatValue.ValueKind == JsonValueKind.Number ? seatValue.GetInt32() : null;
            return new(row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null, seats, row.TryGetProperty("selected", out var selected) && selected.GetBoolean());
        }
        finally { await ClearSearchAsync(token); }
    }

    public async Task<SelectionResult> SelectAsync(string courseCode, CancellationToken token)
    {
        await SearchAsync(courseCode, token);
        try
        {
            var clicked = await ScriptAsync("(() => { const code=" + Q(courseCode) + "; const rows=[...document.querySelectorAll('tr,[class*=row]')].filter(x=>x.innerText&&x.innerText.includes(code)).sort((a,b)=>a.innerText.length-b.innerText.length); let r=rows.find(x=>[...x.querySelectorAll('button,a')].some(b=>b.innerText.trim()==='选课'||b.innerText.trim()==='退课')); if(!r) { const leaf=[...document.querySelectorAll('*')].find(x=>x.children.length===0&&x.textContent.includes(code)); for(let n=leaf;n&&n!==document.body;n=n.parentElement){if([...n.querySelectorAll('button,a')].some(b=>b.innerText.trim()==='选课'||b.innerText.trim()==='退课')){r=n;break;}} } const buttons=r?[...r.querySelectorAll('button,a')]:[]; const b=buttons.find(x=>x.innerText.trim()==='选课'); if(!b) { const chosen=buttons.some(x=>x.innerText.trim()==='退课'); return {ok:chosen,message:chosen?'该课程已经处于已选状态':'未找到该课程的选课按钮'}; } b.click(); return {ok:true}; })()");
            return !clicked.GetProperty("ok").GetBoolean() ? new(false, clicked.GetProperty("message").GetString() ?? "无法选课") : await ReadResultAsync("选课", token);
        }
        finally { await ClearSearchAsync(token); }
    }

    public async Task<SelectionResult> DropAsync(string courseCode, CancellationToken token)
    {
        // 按规则仅在“全部课程”页操作退课，不能在“已选所有课程”页操作。
        await SearchAsync(courseCode, token);
        try
        {
            var clicked = await ScriptAsync("(() => { const r=[...document.querySelectorAll('tr,[class*=row]')].filter(x=>x.innerText&&x.innerText.includes(" + Q(courseCode) + ")).sort((a,b)=>a.innerText.length-b.innerText.length)[0]; const b=r&&[...r.querySelectorAll('button,a')].find(x=>x.innerText.trim()==='退课'); if(!b)return {ok:false,message:'全部课程中未找到该课程的退课按钮'}; b.click(); return {ok:true}; })()");
            if (!clicked.GetProperty("ok").GetBoolean()) return new(false, clicked.GetProperty("message").GetString() ?? "无法退课");
            await Task.Delay(200, token);
            await ScriptAsync("(() => { const b=[...document.querySelectorAll('button')].find(x=>x.innerText.includes('确认退课')); if(b)b.click(); return {ok:!!b}; })()");
            return await ReadResultAsync("退课", token);
        }
        finally { await ClearSearchAsync(token); }
    }

    private static async Task SearchAsync(string code, CancellationToken token)
    {
        EnsureSession();
        await ScriptAsync("(() => { const b=[...document.querySelectorAll('button,a')].find(x=>x.innerText.includes('全部课程')); if(b)b.click(); return {ok:!!b}; })()");
        await Task.Delay(250, token);
        await ClearSearchAsync(token);
        await Task.Delay(120, token);
        var result = await ScriptAsync("(() => { const i=[...document.querySelectorAll('input')].find(x=>(x.placeholder||'').includes('关键词')); if(!i)return {ok:false,message:'未找到关键词搜索框'}; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i," + Q(code) + "); i.dispatchEvent(new Event('input',{bubbles:true})); i.dispatchEvent(new Event('change',{bubbles:true})); return {ok:true}; })()");
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("message").GetString());
        await Task.Delay(1200, token);
    }

    private static Task<JsonElement> ClearSearchAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ScriptAsync("(() => { const i=[...document.querySelectorAll('input')].find(x=>(x.placeholder||'').includes('关键词')); if(!i)return {ok:false}; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,''); i.dispatchEvent(new Event('input',{bubbles:true})); i.dispatchEvent(new Event('change',{bubbles:true})); return {ok:true}; })()");
    }

    private static async Task<SelectionResult> ReadResultAsync(string action, CancellationToken token)
    {
        await Task.Delay(700, token);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var state = await ScriptAsync("(() => ({text:document.body.innerText, dialogs:[...document.querySelectorAll('[role=dialog],.modal,.el-message-box,.ant-modal,.ivu-modal')].map(x=>x.innerText).join('\\n')}))()");
            var text = (state.GetProperty("text").GetString() ?? "") + "\n" + (state.GetProperty("dialogs").GetString() ?? "");
            if (text.Contains(action + "成功")) { await CloseResultDialogAsync(); return new(true, action + "成功"); }
            var message = new[] { "教学班人数已满", "时间冲突", "同课程代码只能选一门" }.FirstOrDefault(text.Contains);
            if (message is not null) { await CloseResultDialogAsync(); return new(false, message); }
            await Task.Delay(350, token);
        }
        await CloseResultDialogAsync();
        return new(false, $"未识别{action}结果，请在登录窗口核对页面提示。");
    }

    private static async Task<JsonElement> ScriptAsync(string script)
    {
        EnsureSession();
        var raw = await LoginSession.Window!.ExecuteAsync(script);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadCourseRowAsync(string courseCode) =>
        await ScriptAsync("(() => { const code=" + Q(courseCode) + "; const leaf=[...document.querySelectorAll('*')].find(x=>x.children.length===0&&x.textContent.includes(code)); const info=leaf&&leaf.closest('td'); const rows=[...document.querySelectorAll('tr,[class*=row]')].filter(x=>x.innerText&&x.innerText.includes(code)).sort((a,b)=>a.innerText.length-b.innerText.length); const r=rows[0]||info&&info.parentElement; if(!r)return {found:false}; const details=(info?info.innerText:r.innerText).split(/\\n+/).map(x=>x.trim()).filter(Boolean); const i=details.findIndex(x=>x.includes(code)); let name=i>0?details[i-1]:null; if(name&&['选课','退课','已选中','换班'].includes(name))name=null; if(name)name=name.replace(/\\s+/g,' ').trim(); const m=[...r.innerText.matchAll(/(\\d+)\\s*\\/\\s*(\\d+)/g)]; const x=m.at(-1); const selected=[...r.querySelectorAll('button,a')].some(x=>x.innerText.trim()==='退课'); return {found:true,name,seats:x?Math.max(0,+x[2]-+x[1]):null,selected}; })()");

    private static Task<JsonElement> CloseResultDialogAsync() =>
        // 教务系统弹窗没有稳定的 CSS 容器标识；按可见的精确按钮文本关闭，绝不点击“选课申请”。
        ScriptAsync("(() => { const buttons=[...document.querySelectorAll('button,a')].filter(x=>x.offsetParent!==null&&['关闭','确定','完成'].includes(x.innerText.trim())); const b=buttons.at(-1); if(b)b.click(); return {closed:!!b}; })()");

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static void EnsureSession()
    {
        if (LoginSession.Window is null || !LoginSession.IsAuthenticated)
            throw new InvalidOperationException("请先在内置登录窗口完成认证，并进入选课页面。");
    }
}
