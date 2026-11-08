using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace UstcCourseAssistant.Core.Authentication;

internal static class LoginFields
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "login-croypto", "login-page-flowkey", "riskSystemSwitch", "recaptchaVendor",
        "riskSystemGroupId", "targetSystem", "siteId", "login-error-code",
        "login-rule-type", "current-login-type", "phone-number", "login-error-msg"
    };


    internal static Dictionary<string, string> Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string? name, string value)
        {
            if (name is null || !KnownFields.Contains(name)) return;
            if (result.TryGetValue(name, out var existing))
            {
                if (!string.Equals(existing, value, StringComparison.Ordinal))
                    throw new InvalidOperationException($"登录页面包含冲突字段：{name}。");
                return;
            }
            result.Add(name, value);
        }

        foreach (var node in document.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
        {
            if (node.AncestorsAndSelf().Any(n => n.Name is "script" or "style")) continue;
            var id = WebUtility.HtmlDecode(node.GetAttributeValue("id", ""));
            if (node.Name == "input")
            {
                if (!string.Equals(node.GetAttributeValue("type", ""), "hidden", StringComparison.OrdinalIgnoreCase)) continue;
                var value = WebUtility.HtmlDecode(node.GetAttributeValue("value", ""));
                Add(id, value);
                Add(WebUtility.HtmlDecode(node.GetAttributeValue("name", "")), value);
            }
            else if (id is not null && KnownFields.Contains(id))
            {
                var text = new StringBuilder();
                foreach (var child in node.Descendants().Where(n => n.NodeType == HtmlNodeType.Text))
                {
                    if (!child.Ancestors().Any(n => n.Name is "script" or "style")) text.Append(child.InnerText);
                }
                Add(id, WebUtility.HtmlDecode(text.ToString()).Trim());
            }
        }
        return result;
    }
}
