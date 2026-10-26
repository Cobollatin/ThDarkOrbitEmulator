using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalEconomyHeader
{
    // Appended after page-specific styles so the local menus share one visual system.
    private const string MenuStyle = """
        <style id="localMenuTheme">
        :root{color-scheme:dark}*{box-sizing:border-box}
        html{background:#060d18}body{margin:0;background:#060d18;color:#e2e8f0;font:16px/1.55 system-ui,Segoe UI,Arial,sans-serif;min-height:100vh}
        body>main,body>div>main,main{max-width:1200px!important;margin:32px auto!important;padding:24px!important}
        h1,h2,h3{color:#f1f5f9;line-height:1.25}h1{margin-top:0}p,li,dt,dd,label,small{color:#e2e8f0}
        a{color:#38bdf8}a:hover,a:focus-visible{color:#7dd3fc}a[aria-current="page"]{color:#e0f2fe;font-weight:700}
        article,section,.card,.menuSection,#pilotCard,fieldset{background:#0f172a;border:1px solid #26364e;border-radius:12px;padding:20px;margin:0 0 18px;box-shadow:0 10px 28px rgba(0,0,0,.16)}
        nav{display:flex;flex-wrap:wrap;gap:10px;margin:18px 0}nav a{display:inline-block;padding:8px 12px;border:1px solid #26364e;border-radius:8px;background:#0f172a;text-decoration:none}
        form{margin:12px 0}input:not([type="hidden"]),select,textarea{max-width:100%;padding:9px 12px;border:1px solid #475569;border-radius:8px;background:#111d32;color:#e2e8f0;font:inherit}
        input::placeholder,textarea::placeholder{color:#94a3b8}input:focus-visible,select:focus-visible,textarea:focus-visible,button:focus-visible,a:focus-visible{outline:2px solid #38bdf8;outline-offset:2px}
        button,input[type="submit"],input[type="button"]{padding:9px 16px;border:1px solid #0891b2;border-radius:8px;background:#0e7490;color:#f8fafc;font:inherit;font-weight:600;cursor:pointer}
        button:hover,input[type="submit"]:hover,input[type="button"]:hover{background:#0891b2}button:disabled,input:disabled{opacity:.55;cursor:not-allowed}
        table{width:100%;border-collapse:collapse;background:#0f172a;border:1px solid #26364e;border-radius:10px;overflow:hidden}th,td{padding:10px 12px;border-bottom:1px solid #26364e;text-align:left}th{background:#17243b;color:#f1f5f9}tr:nth-child(even){background:#111d32}
        code,pre{color:#bae6fd}pre{overflow:auto}hr{border:0;border-top:1px solid #26364e}
        #economyBalances{position:relative;padding:12px max(24px,calc((100vw - 1152px)/2));background:#0f172a;color:#e2e8f0;border-bottom:1px solid #26364e;font-size:14px}
        #economyBalances a{color:#38bdf8}
        @media(max-width:700px){main{margin:12px auto!important;padding:16px!important}article,section,.card,.menuSection,#pilotCard,fieldset{padding:16px}table{display:block;overflow-x:auto}#economyBalances{padding:12px 16px}}
        </style>
        """;

    public static string Add(string page, IReadOnlyList<EconomyBalance> balances)
    {
        var rows = string.Join(" · ", balances.Select(balance =>
            $"{HtmlEncoder.Default.Encode(balance.AssetId)}: {balance.Amount:N0}"));
        var header = $"<header id=\"economyBalances\"><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Menu</a> · {rows}</header>";
        return page.Replace("</head>", MenuStyle + "</head>", StringComparison.OrdinalIgnoreCase)
            .Replace("<body>", "<body>" + header, StringComparison.OrdinalIgnoreCase);
    }
}
