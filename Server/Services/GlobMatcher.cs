using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ModGod.Services;

/// <summary>
/// Simple glob pattern matcher for file paths.
/// Supports: *, **, ?
/// </summary>
public static class GlobMatcher
{
    // Cache compiled regexes for performance (pattern -> compiled regex)
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    
    /// <summary>
    /// Check if a path matches a glob pattern.
    /// Patterns:
    ///   * = any characters except /
    ///   ** = any characters including /
    ///   ? = single character
    /// </summary>
    public static bool IsMatch(string path, string pattern)
    {
        // Normalize both path and pattern
        path = NormalizePath(path);
        pattern = NormalizePath(pattern);
        
        // Empty pattern matches nothing
        if (string.IsNullOrEmpty(pattern))
            return false;
        
        try
        {
            // Get or create cached regex
            var regex = _regexCache.GetOrAdd(pattern, p => GlobToRegex(p));
            return regex.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern caused catastrophic backtracking - treat as no match
            return false;
        }
    }
    
    /// <summary>
    /// Check if a path matches any of the given patterns.
    /// </summary>
    public static bool IsMatchAny(string path, IEnumerable<string> patterns)
    {
        return patterns.Any(p => IsMatch(path, p));
    }
    
    /// <summary>
    /// Clear the regex cache (useful if patterns change at runtime)
    /// </summary>
    public static void ClearCache()
    {
        _regexCache.Clear();
    }
    
    /// <summary>
    /// Convert a glob pattern to a compiled regex.
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        var regexPattern = "^";
        var i = 0;
        
        while (i < pattern.Length)
        {
            var c = pattern[i];
            
            if (c == '*')
            {
                // Check for **
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // ** matches any path segment including /
                    // Check if followed by /
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        regexPattern += "(.*/)?"; // Match any path prefix (optional)
                        i += 3;
                    }
                    else
                    {
                        regexPattern += ".*"; // Match anything
                        i += 2;
                    }
                }
                else
                {
                    // * matches any characters except /
                    regexPattern += "[^/]*";
                    i++;
                }
            }
            else if (c == '?')
            {
                // ? matches single character except /
                regexPattern += "[^/]";
                i++;
            }
            else if (c == '.')
            {
                regexPattern += "\\.";
                i++;
            }
            else if (c == '/' || c == '\\')
            {
                regexPattern += "/";
                i++;
            }
            else if (c == '[' || c == ']' || c == '(' || c == ')' || c == '{' || c == '}' || 
                     c == '+' || c == '^' || c == '$' || c == '|')
            {
                // Escape regex special characters
                regexPattern += "\\" + c;
                i++;
            }
            else
            {
                regexPattern += c;
                i++;
            }
        }
        
        regexPattern += "$";
        
        // Add timeout to prevent catastrophic backtracking
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    }
    
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}

