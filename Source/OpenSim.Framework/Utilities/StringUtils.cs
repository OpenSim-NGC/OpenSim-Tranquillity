using System.Collections;
using System.Text.RegularExpressions;

namespace OpenSim.Framework.Utilities;

public static class StringUtils
{
    /// <summary>
    ///     Because Escaping the sql might cause it to go over the max length
    ///     DO NOT USE THIS ON JSON STRINGS!!! IT WILL BREAK THE DESERIALIZATION!!!
    /// </summary>
    /// <param name="usString"></param>
    /// <param name="maxLength"></param>
    /// <returns></returns>
    public static string MySqlEscape (this string usString, int maxLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        
        if (usString == null) {
            return null;
        }
        // SQL Encoding for MySQL Recommended here:
        // http://au.php.net/manual/en/function.mysql-real-escape-string.php
        // it escapes \r, \n, \x00, \x1a, baskslash, single quotes, and double quotes
        string returnvalue = Regex.Replace (usString, @"[\r\n\x00\x1a\\'""]", @"\$0");
        if ((maxLength != 0) && (returnvalue.Length > maxLength))
            returnvalue = returnvalue.Substring (0, maxLength);
        return returnvalue;
    }

    /// From http://www.c-sharpcorner.com/UploadFile/mahesh/RandomNumber11232005010428AM/RandomNumber.aspx
    /// <summary>
    ///     Generates a random string with the given length
    /// </summary>
    /// <param name="size">Size of the string</param>
    /// <param name="lowerCase">If true, generate lowercase string</param>
    /// <returns>Random string</returns>
    public static string RandomString (int size, bool lowerCase)
    {
        string builder = "t";
        int off = lowerCase ? 'a' : 'A';
        int j;
        for (int i = 0; i < size; i++) {
            j = Util.RandomClass.Next (25);
            builder += (char)(j + off);
        }

        return builder;
    }

    public static string [] AlphanumericSort (List<string> list)
    {
        string [] nList = list.ToArray ();
        Array.Sort (nList, new AlphanumComparatorFast ());
        return nList;
    }

    public class AlphanumComparatorFast : IComparer
    {
        public int Compare (object x, object y)
        {
            string s1 = x as string;
            if (s1 == null)
                return 0;

            string s2 = y as string;
            if (s2 == null)
                return 0;


            int len1 = s1.Length;
            int len2 = s2.Length;
            int marker1 = 0;
            int marker2 = 0;

            // Walk through two the strings with two markers.
            while (marker1 < len1 && marker2 < len2) {
                char ch1 = s1 [marker1];
                char ch2 = s2 [marker2];

                // Some buffers we can build up characters in for each chunk.
                char [] space1 = new char [len1];
                int loc1 = 0;
                char [] space2 = new char [len2];
                int loc2 = 0;

                // Walk through all following characters that are digits or
                // characters in BOTH strings starting at the appropriate marker.
                // Collect char arrays.
                do {
                    space1 [loc1++] = ch1;
                    marker1++;

                    if (marker1 < len1) {
                        ch1 = s1 [marker1];
                    } else {
                        break;
                    }
                } while (char.IsDigit (ch1) == char.IsDigit (space1 [0]));

                do {
                    space2 [loc2++] = ch2;
                    marker2++;

                    if (marker2 < len2) {
                        ch2 = s2 [marker2];
                    } else {
                        break;
                    }
                } while (char.IsDigit (ch2) == char.IsDigit (space2 [0]));

                // If we have collected numbers, compare them numerically.
                // Otherwise, if we have strings, compare them alphabetically.
                string str1 = new string (space1);
                string str2 = new string (space2);

                int result;

                if (char.IsDigit (space1 [0]) && char.IsDigit (space2 [0])) {
                    int thisNumericChunk = int.Parse (str1);
                    int thatNumericChunk = int.Parse (str2);
                    result = thisNumericChunk.CompareTo (thatNumericChunk);
                } else {
                    result = string.Compare (str1, str2, StringComparison.Ordinal);
                }

                if (result != 0) {
                    return result;
                }
            }
            return len1 - len2;
        }
    }

    public static List<string> SizeSort (List<string> functionKeys, bool smallestToLargest)
    {
        functionKeys.Sort ((a, b) => { return a.Length.CompareTo (b.Length); });
        if (!smallestToLargest)
            functionKeys.Reverse (); //Flip the order then
        return functionKeys;
    }
}