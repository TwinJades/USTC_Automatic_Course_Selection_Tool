using System.Text.Json;

namespace UstcCourseAssistant.Route2.Services;

public static class LoginFormAutomation
{
    public static string BuildTeachingLoginEntryScript() =>
        """
        (() => {
          const visible = element => {
            const style = window.getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && rect.width > 0 && rect.height > 0 && !element.disabled;
          };
          const metadata = element => [
            element.innerText, element.textContent, element.value, element.title,
            element.getAttribute('aria-label'), element.id, element.className, element.getAttribute('href')
          ].filter(Boolean).join(' ').replace(/\s+/g, ' ').trim();
          const candidates = Array.from(document.querySelectorAll(
            'a, button, [role="button"], [onclick], input[type="button"], input[type="submit"]'))
            .filter(visible);

          const trustedIdentityLink = candidates.find(element => {
            const href = element.getAttribute('href');
            if (!href) return false;
            try {
              return new URL(href, window.location.href).hostname.toLowerCase() === 'id.ustc.edu.cn';
            } catch { return false; }
          });
          const identityControl = trustedIdentityLink || candidates.find(element =>
            /(统一身份认证|统一认证|统一登录|身份认证)/i.test(metadata(element)));
          const fallbackLoginControl = candidates.find(element =>
            /^(登录|立即登录|进入教务系统|前往登录)$/i.test(metadata(element)));
          const target = identityControl || fallbackLoginControl;
          if (!target) return 'other';
          target.click();
          return 'entry-clicked';
        })()
        """;

    public static string BuildClassificationScript() =>
        """
        (() => {
          const inputs = Array.from(document.querySelectorAll('input'));
          const metadata = input => [input.id, input.name, input.placeholder, input.autocomplete]
            .filter(Boolean).join(' ').toLowerCase();
          const isInteractiveVerificationInput = input =>
            ['text', 'tel', 'number'].includes((input.type || 'text').toLowerCase());
          const verificationInput = inputs.find(input =>
            isInteractiveVerificationInput(input)
            && /(captcha|otp|sms|verify|verification|验证码|短信|动态码)/i.test(metadata(input)));
          if (verificationInput) return 'verification';

          const password = inputs.find(input => input.type === 'password');
          const account = inputs.find(input =>
            input !== password && /(username|user|account|login|学号|账号|工号|gid)/i.test(metadata(input)));
          if (password && account) return 'login';

          const text = (document.body?.innerText || '').toLowerCase();
          if (!password && /(短信验证|验证码|新设备|二次认证|动态码|安全验证|otp|verification code)/i.test(text)) {
            return 'verification';
          }
          return 'other';
        })()
        """;

    public static string BuildSubmissionScript(string userName, string password)
    {
        var userNameJson = JsonSerializer.Serialize(userName);
        var passwordJson = JsonSerializer.Serialize(password);
        return $$"""
        (() => {
          const inputs = Array.from(document.querySelectorAll('input'));
          const metadata = input => [input.id, input.name, input.placeholder, input.autocomplete]
            .filter(Boolean).join(' ').toLowerCase();
          const isInteractiveVerificationInput = input =>
            ['text', 'tel', 'number'].includes((input.type || 'text').toLowerCase());
          const verificationInput = inputs.find(input =>
            isInteractiveVerificationInput(input)
            && /(captcha|otp|sms|verify|verification|验证码|短信|动态码)/i.test(metadata(input)));
          if (verificationInput) return 'verification';

          const passwordInput = inputs.find(input => input.type === 'password');
          const accountInput = inputs.find(input =>
            input !== passwordInput && /(username|user|account|login|学号|账号|工号|gid)/i.test(metadata(input)));
          if (!passwordInput || !accountInput) return 'other';

          const setValue = (input, value) => {
            const descriptor = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value');
            descriptor.set.call(input, value);
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
          };
          setValue(accountInput, {{userNameJson}});
          setValue(passwordInput, {{passwordJson}});

          const form = passwordInput.form || accountInput.form;
          if (form?.requestSubmit) {
            form.requestSubmit();
            return 'submitted';
          }
          const submit = document.querySelector('button[type="submit"], input[type="submit"]');
          if (!submit) return 'filled';
          submit.click();
          return 'submitted';
        })()
        """;
    }

    public static string ParseResult(string jsonResult)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(jsonResult) ?? "other";
        }
        catch (JsonException)
        {
            return "other";
        }
    }
}
