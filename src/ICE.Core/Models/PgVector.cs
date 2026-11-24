using System.Globalization;

namespace ICE.Core.Models;

/// <summary>pgvector's text format (<c>[0.1,0.2,...]</c>), cast to <c>vector</c> in SQL; avoids a driver plugin.</summary>
public static class PgVector
{
    public static string ToText(float[] vector)
        => "[" + string.Join(",", vector.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))) + "]";
}
