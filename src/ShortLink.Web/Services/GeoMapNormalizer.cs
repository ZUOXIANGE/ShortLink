namespace ShortLink.Web.Services;

/// <summary>
/// 把采集到的国家/省份值对齐到地图区域。
/// <para>
/// 采集侧为了条件路由不做任何改写：MaxMind 给 ISO2 和「广东省」这类显示名，
/// 请求头兜底则可能是任意字符串。地图匹配只做展示口径的归一，不回头修改入库值。
/// </para>
/// </summary>
public static class GeoMapNormalizer
{
    private static readonly string[] ProvinceSuffixes =
    [
        "特别行政区",
        "维吾尔自治区",
        "壮族自治区",
        "回族自治区",
        "自治区",
        "省",
        "市",
    ];

    // MaxMind 在 zh-CN 缺失时会回退英文省名，这里把常见英文名折回中文，保证同一张地图能同时匹配中英文。
    private static readonly Dictionary<string, string> ProvinceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Beijing"] = "北京",
        ["Tianjin"] = "天津",
        ["Hebei"] = "河北",
        ["Shanxi"] = "山西",
        ["NeiMongol"] = "内蒙古",
        ["InnerMongolia"] = "内蒙古",
        ["Liaoning"] = "辽宁",
        ["Jilin"] = "吉林",
        ["Heilongjiang"] = "黑龙江",
        ["Shanghai"] = "上海",
        ["Jiangsu"] = "江苏",
        ["Zhejiang"] = "浙江",
        ["Anhui"] = "安徽",
        ["Fujian"] = "福建",
        ["Jiangxi"] = "江西",
        ["Shandong"] = "山东",
        ["Henan"] = "河南",
        ["Hubei"] = "湖北",
        ["Hunan"] = "湖南",
        ["Guangdong"] = "广东",
        ["Guangxi"] = "广西",
        ["Hainan"] = "海南",
        ["Chongqing"] = "重庆",
        ["Sichuan"] = "四川",
        ["Guizhou"] = "贵州",
        ["Yunnan"] = "云南",
        ["Xizang"] = "西藏",
        ["Tibet"] = "西藏",
        ["Shaanxi"] = "陕西",
        ["Gansu"] = "甘肃",
        ["Qinghai"] = "青海",
        ["Ningxia"] = "宁夏",
        ["Xinjiang"] = "新疆",
        ["Taiwan"] = "台湾",
        ["HongKong"] = "香港",
        ["Macau"] = "澳门",
        ["Macao"] = "澳门",
    };

    /// <summary>返回大写 ISO2；无法识别（如 CDN 头给了国家全名）时返回 null，由界面计入未匹配。</summary>
    public static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && char.IsAsciiLetter(trimmed[1]))
        {
            return trimmed.ToUpperInvariant();
        }

        return trimmed switch
        {
            "中国" or "中华人民共和国" => "CN",
            _ when trimmed.Equals("China", StringComparison.OrdinalIgnoreCase) => "CN",
            _ => null,
        };
    }

    /// <summary>
    /// 省份归一：去掉「省/市/自治区/特别行政区」后缀并移除空格。
    /// 不做模糊匹配——「海南藏族自治州」不能被当成「海南」。
    /// </summary>
    public static string NormalizeProvince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var name = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var suffix in ProvinceSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return ProvinceAliases.GetValueOrDefault(name, name);
    }
}
