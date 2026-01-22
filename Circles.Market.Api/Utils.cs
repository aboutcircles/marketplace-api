namespace Circles.Market.Api;

public class Utils
{
    public static string NormalizeAddr(string a)
    {
        bool empty = string.IsNullOrWhiteSpace(a);
        if (empty)
        {
            throw new ArgumentException("Address is required");
        }

        string t = a.Trim();
        bool looksHex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool lengthOk = t.Length == 42;
        if (!looksHex || !lengthOk)
        {
            throw new ArgumentException("Address must be 42 chars and start with 0x");
        }

        // Enforce hex characters for the 40 nybbles
        for (int i = 2; i < 42; i++)
        {
            char c = t[i];
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                throw new ArgumentException("Address must contain only hexadecimal characters 0-9a-f");
            }
        }

        return t.ToLowerInvariant();
    }
}