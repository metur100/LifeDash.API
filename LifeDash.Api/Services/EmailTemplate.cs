using System.Net;
using System.Text;

namespace LifeDash.Api.Services;

/// <summary>
/// One shared HTML layout for every reminder email, so appointments, birthdays,
/// finance and contract reminders all look the same and always show the full
/// detail table rather than a one-line plain-text sentence.
/// </summary>
public static class EmailTemplate
{
    public static string Render(
        string accentHex,
        string kicker,
        string title,
        string leadLine,
        IEnumerable<(string Label, string Value)> rows,
        string footerNote)
    {
        var rowsHtml = new StringBuilder();
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            rowsHtml.Append($@"
              <tr>
                <td style=""padding:9px 0;border-top:1px solid #e6ebf1;color:#5b6b7c;font-size:13px;width:38%;vertical-align:top;"">{Enc(label)}</td>
                <td style=""padding:9px 0;border-top:1px solid #e6ebf1;color:#132638;font-size:14px;font-weight:600;vertical-align:top;"">{Enc(value)}</td>
              </tr>");
        }

        return $@"<!doctype html>
<html>
<body style=""margin:0;padding:24px;background:#f2f5f9;font-family:Segoe UI,Arial,sans-serif;"">
  <table role=""presentation"" width=""100%"" style=""max-width:560px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e2e8f0;"">
    <tr>
      <td style=""background:{accentHex};padding:16px 24px;"">
        <span style=""display:block;color:#ffffff;opacity:0.85;font-size:11px;letter-spacing:0.08em;text-transform:uppercase;font-weight:700;"">{Enc(kicker)}</span>
        <span style=""display:block;color:#ffffff;font-size:19px;font-weight:700;margin-top:4px;"">{Enc(title)}</span>
      </td>
    </tr>
    <tr>
      <td style=""padding:20px 24px 4px;"">
        <p style=""margin:0 0 6px;color:#132638;font-size:15px;line-height:1.5;"">{Enc(leadLine)}</p>
      </td>
    </tr>
    <tr>
      <td style=""padding:4px 24px 20px;"">
        <table role=""presentation"" width=""100%"" style=""border-collapse:collapse;"">
          {rowsHtml}
        </table>
      </td>
    </tr>
    <tr>
      <td style=""padding:14px 24px;background:#f7f9fc;border-top:1px solid #e6ebf1;"">
        <p style=""margin:0;color:#7c8b9c;font-size:12px;line-height:1.5;"">{Enc(footerNote)}</p>
      </td>
    </tr>
  </table>
</body>
</html>";
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
