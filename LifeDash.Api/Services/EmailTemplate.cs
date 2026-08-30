using System.Net;
using System.Text;

namespace LifeDash.Api.Services;

/// <summary>
/// One shared HTML layout for every reminder email - appointments, birthdays,
/// finance and contract reminders all get the same clean card design with a
/// per-category accent color, instead of a one-line plain-text sentence.
/// Table-based + inline styles throughout for Outlook/Gmail compatibility;
/// rounded corners / shadow are progressive enhancement (Outlook just shows
/// square corners, everything else still works).
/// </summary>
public static class EmailTemplate
{
    private const string FontStack = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    public static string Render(
        string accentHex,
        string emoji,
        string kicker,
        string title,
        string badgeText,
        string leadLine,
        IEnumerable<(string Label, string Value)> rows,
        string? actionUrl,
        string? actionLabel,
        string footerNote)
    {
        var badgeBg = LightTint(accentHex, 0.85);
        var iconBg = LightTint(accentHex, 0.88);

        var rowsHtml = new StringBuilder();
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            rowsHtml.Append($@"
              <tr>
                <td style=""padding:12px 0;border-top:1px solid #edf1f6;color:#6b7684;font-size:13px;font-family:{FontStack};width:36%;vertical-align:top;line-height:1.5;"">{Enc(label)}</td>
                <td style=""padding:12px 0;border-top:1px solid #edf1f6;color:#182233;font-size:14.5px;font-weight:600;font-family:{FontStack};vertical-align:top;line-height:1.5;"">{Enc(value)}</td>
              </tr>");
        }

        var badgeHtml = string.IsNullOrWhiteSpace(badgeText) ? "" : $@"
              <tr>
                <td align=""center"" style=""padding:2px 0 18px;"">
                  <span style=""display:inline-block;background:{badgeBg};color:{accentHex};font-family:{FontStack};font-size:12.5px;font-weight:700;padding:6px 16px;border-radius:999px;letter-spacing:0.01em;"">{Enc(badgeText)}</span>
                </td>
              </tr>";

        var buttonHtml = string.IsNullOrWhiteSpace(actionUrl) ? "" : $@"
    <tr>
      <td align=""center"" style=""padding:26px 32px 4px;"">
        <table role=""presentation"" cellpadding=""0"" cellspacing=""0"">
          <tr>
            <td align=""center"" bgcolor=""{accentHex}"" style=""border-radius:10px;"">
              <a href=""{WebUtility.HtmlEncode(actionUrl)}"" target=""_blank"" style=""display:inline-block;padding:12px 26px;font-family:{FontStack};font-size:14.5px;font-weight:700;color:#ffffff;text-decoration:none;border-radius:10px;"">{Enc(actionLabel ?? "In LifeDash öffnen")} &rarr;</a>
            </td>
          </tr>
        </table>
      </td>
    </tr>";

        return $@"<!doctype html>
<html lang=""de"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<meta name=""color-scheme"" content=""light"">
</head>
<body style=""margin:0;padding:0;background:#eef1f6;-webkit-text-size-adjust:100%;"">
  <div style=""display:none;max-height:0;overflow:hidden;opacity:0;"">{Enc(leadLine)}&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;&#8203;</div>
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#eef1f6;padding:32px 16px;"">
    <tr>
      <td align=""center"">
        <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" style=""width:560px;max-width:100%;background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 12px 32px rgba(20,30,50,0.10);"">
          <tr>
            <td style=""height:5px;background:{accentHex};font-size:0;line-height:0;"">&nbsp;</td>
          </tr>
          <tr>
            <td style=""padding:34px 32px 6px;"" align=""center"">
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"">
                <tr>
                  <td align=""center"" bgcolor=""{iconBg}"" style=""width:56px;height:56px;border-radius:16px;font-size:26px;line-height:56px;"">{emoji}</td>
                </tr>
              </table>
            </td>
          </tr>
          <tr>
            <td align=""center"" style=""padding:14px 32px 2px;"">
              <span style=""display:block;color:{accentHex};font-family:{FontStack};font-size:12px;font-weight:700;letter-spacing:0.08em;text-transform:uppercase;"">{Enc(kicker)}</span>
            </td>
          </tr>
          <tr>
            <td align=""center"" style=""padding:6px 32px 16px;"">
              <span style=""display:block;color:#101828;font-family:{FontStack};font-size:22px;font-weight:800;line-height:1.3;"">{Enc(title)}</span>
            </td>
          </tr>
          {badgeHtml}
          <tr>
            <td style=""padding:0 32px;"">
              <p style=""margin:0;color:#3d4759;font-family:{FontStack};font-size:15px;line-height:1.6;text-align:center;"">{Enc(leadLine)}</p>
            </td>
          </tr>
          <tr>
            <td style=""padding:22px 32px 4px;"">
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:collapse;"">
                {rowsHtml}
              </table>
            </td>
          </tr>
          {buttonHtml}
          <tr>
            <td style=""padding:28px 32px 26px;"">&nbsp;</td>
          </tr>
          <tr>
            <td style=""padding:16px 32px;background:#f7f9fc;border-top:1px solid #edf1f6;"" align=""center"">
              <p style=""margin:0 0 4px;color:#8a94a3;font-family:{FontStack};font-size:12px;line-height:1.5;text-align:center;"">{Enc(footerNote)}</p>
              <span style=""display:block;color:#b3bccb;font-family:{FontStack};font-size:11px;font-weight:700;letter-spacing:0.06em;text-transform:uppercase;margin-top:6px;"">LifeDash</span>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Mixes a hex color toward white by `amount` (0..1) for tinted badge/icon backgrounds.</summary>
    private static string LightTint(string hex, double amount)
    {
        var h = hex.TrimStart('#');
        var r = Convert.ToInt32(h.Substring(0, 2), 16);
        var g = Convert.ToInt32(h.Substring(2, 2), 16);
        var b = Convert.ToInt32(h.Substring(4, 2), 16);
        r += (int)((255 - r) * amount);
        g += (int)((255 - g) * amount);
        b += (int)((255 - b) * amount);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
