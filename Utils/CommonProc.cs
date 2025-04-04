using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Diagnostics;

namespace DocGOST.Utils
{
    class AbortException : Exception
    {
        public AbortException() : base() { }
        public AbortException(string Message) : base(Message) { }
    };
    class AssertException : Exception
    {
        public AssertException() : base() { }
        public AssertException(string Message) : base(Message) { }
    };
    class InfoException : Exception
    {
        public InfoException() : base() { }
        public InfoException(string Message) : base(Message) { }
    };

    class ConfigurationFile
    {
        public static Dictionary<string, string> LoadFromFile(string FileName) {
            var rslt = new Dictionary<string, string>();
            using (StreamReader reader = new StreamReader(FileName)) {
                while (!reader.EndOfStream) {
                    string configLine = reader.ReadLine().Trim();
                    if ((configLine == "") || configLine.StartsWith("#"))
                        continue;
                    int cp = configLine.IndexOf(':');
                    if (cp == -1)
                        continue;
                    var key = configLine.Substring(0, cp).TrimEnd();
                    var value = configLine.Substring(cp + 1).TrimStart();
                    rslt[key] = value;
                }
            }
            return rslt;
        }

        public static void SaveToFile(string filename, Dictionary<string, string> prms) {
            using (StreamWriter writer = new StreamWriter(filename)) {
                foreach (var kv in prms)
                    writer.WriteLine(kv.Key + " : " + kv.Value);
            }
        }
    }

    public static class CommonProc
    {
        public static string GetCurrentMethod([CallerMemberName] string caller = null) {
            return caller;
        }

        public static void RaiseIfTrue(bool condition, string msg)
        {
            if (condition)
                throw new AssertException(msg);
        }

        public static void RaiseIfTimeElapsed(int startTick, int timeout_ms)
        {
            if (Environment.TickCount - startTick > timeout_ms)
                throw new InfoException("Превышено максимальное время завершения операции.");
        }

        public static void DisposeAndNil<T> (ref T obj) {
            if (obj == null) return;
            if (obj is IDisposable dis)
                try { 
                    dis.Dispose();
                } catch { }
            obj = default;
        }

        public static bool IsInRange<T>(this T value, T min, T max) where T : System.IComparable<T> {
            if (value.CompareTo(min) < 0) return false;
            if (value.CompareTo(max) > 0) return false;
            return true;
        }
    }

    public static class StringExtensions
    { 
        public static int ToInt32(this string value, int min, int max) {
            int vi = int.Parse(value);
            if (!CommonProc.IsInRange(vi, min, max)) {
                throw new ArgumentOutOfRangeException($"Число '{value}' находится вне допустимого диапазона {min}..{max}");
            }
            return vi;
        }

        public static int ToInt32(this string value) {
            return int.Parse(value);
        }
    }

    public static class DoubleIC
    {
        public static string ToStringIC(this Double v) {
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public static string ToStringIC(this Double v, string format) {
            return v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        public static double ParseDot(string s) {
            return Double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }
        public static double ParseDotAndComma(string s) {
            return Double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        }
        public static bool TryParseDot(string s, out double result) {
            return Double.TryParse(s, NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        public static bool TryParseDotAndComma(string s, out double result) {
            return Double.TryParse(s.Replace(',', '.'), NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
    }

    public static class DictionaryExtensions
    {
        public static TValue GetValueDef<TKey, TValue>(this Dictionary<TKey, TValue> map, TKey key, TValue defValue) {
            return map.TryGetValue(key, out TValue v) ? v : defValue;
        }

        public static TValue GetValueRE<TKey, TValue>(this Dictionary<TKey, TValue> map, TKey key) {
            try {
                return map[key];
            } catch (KeyNotFoundException ex) {
                throw new KeyNotFoundException(ex.Message + " (" + key + ")");
            }
        }
    }

    public class CsvReader : IDisposable
    {
        const string EUnexpectedEof = "Неожиданный конец файла";

        StreamReader reader;
        public int nLine { get; private set; }
        double[] values = new double[2];
        string[] svalues = new string[0];
        string fileName;
        char delimiter;

        public CsvReader(string fileName, char delimiter = ';') {
            this.fileName = fileName;
            this.delimiter = delimiter;
            reader = new StreamReader(fileName, Encoding.GetEncoding(1251));
            nLine = 0;
        }

        public bool Eof() {
            return reader.EndOfStream;
        }

        public string ReadLine() {
            if (Eof()) throw new Exception(EUnexpectedEof);

            string s = reader.ReadLine().Trim();
            nLine++;
            return s;
        }

        public double[] ReadLineAsDoubles(int expectedNItems) {
            if (Eof()) throw new Exception(EUnexpectedEof);

            string s = reader.ReadLine().Trim();
            nLine++;

            var SA = s.Split(delimiter);
            if (SA.Length != expectedNItems && expectedNItems != -1) throw new Exception($"Количество значений ({SA.Length}) в строке отличается от ожидаемого ({expectedNItems})");
            if (values.Length != SA.Length)
                Array.Resize(ref values, SA.Length);
            for (int i = 0; i < SA.Length; i++) {
                values[i] = DoubleIC.ParseDotAndComma(SA[i]);
            }

            return values;
        }

        public string[] ReadLineAsStrings(int expectedNItems = -1, bool TrimEveryValue = false) {
            if (Eof()) throw new Exception(EUnexpectedEof);

            string s = reader.ReadLine()/*.Trim()*/;
            nLine++;

            var SA = s.Split(delimiter);
            if (SA.Length != expectedNItems && expectedNItems != -1) throw new Exception($"Количество значений ({SA.Length}) в строке отличается от ожидаемого ({expectedNItems})");
            if (svalues.Length != SA.Length)
                Array.Resize(ref svalues, SA.Length);
            for (int i = 0; i < SA.Length; i++) {
                svalues[i] = TrimEveryValue ? SA[i].Trim() : SA[i];
                if (svalues[i].StartsWith("\"") && svalues[i].EndsWith("\"")) {
                    svalues[i] = svalues[i].Substring(1, svalues[i].Length - 2).Replace("\"\"", "\"");
                }
            }
            return svalues;
        }

        public string MakeExceptionString(string lastExceptionMessage) {
            return $"Ошибка при открытии файла {Path.GetFileName(fileName)}: Ошибка в строке {nLine}: {lastExceptionMessage}";
        }

        public static double[] MakeDoublesFromString(string sData, char delimiter = ';') {
            if (sData == "") return new double[0];
            var sarray = sData.Split(delimiter);
            var drslt = new double[sarray.Length];
            for (int i = 0; i < sarray.Length; i++) {
                drslt[i] = DoubleIC.ParseDot(sarray[i]);
            }
            return drslt;
        }

        public static int[] MakeIntsFromString(string sData, char delimiter = ';') {
            if (sData == "") return new int[0];
            var sarray = sData.Split(delimiter);
            var irslt = new int[sarray.Length];
            for (int i = 0; i < sarray.Length; i++) {
                irslt[i] = int.Parse(sarray[i]);
            }
            return irslt;
        }

        public void Dispose() {
            reader.Dispose();
        }
    }

    public class CsvWriter : IDisposable
    {
        StreamWriter writer;
        public int nLine { get; private set; }
        string fileName;
        char delimiter;

        public CsvWriter(string fileName, char delimiter = ';') {
            this.fileName = fileName;
            this.delimiter = delimiter;
            writer = new StreamWriter(fileName, false, Encoding.GetEncoding(1251));
            nLine = 0;
        }

        public void WriteLine(string data) {
            writer.WriteLine(data);
            nLine++;
        }

        public void WriteLineAsDoubles(double[] values) {
            if (values.Length == 0) return;
            for (int i = 0; i < values.Length - 1; i++) {
                writer.Write(DoubleIC.ToStringIC(values[i]));
                writer.Write(delimiter);
            }
            writer.WriteLine(DoubleIC.ToStringIC(values[values.Length - 1]));
        }

        public static string MakeStringFromDoubles(IEnumerable<double> values, char delimiter = ';') {
            StringBuilder sb = new StringBuilder();
            foreach (var d in values) {
                sb.Append(DoubleIC.ToStringIC(d));
                sb.Append(delimiter);
            }
            if (sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            return sb.ToString();
            //sb.Append("\r\n");
        }

        public static string MakeStringFromInts(IEnumerable<int> values, char delimiter = ';') {
            StringBuilder sb = new StringBuilder();
            foreach (var d in values) {
                sb.Append(d.ToString());
                sb.Append(delimiter);
            }
            if (sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            return sb.ToString();
            //sb.Append("\r\n");
        }

        public static string MakeStringFromStrings(IEnumerable<string> values, char delimiter = ';') {
            StringBuilder sb = new StringBuilder();
            foreach (var s in values) {
                int idx = s.IndexOf(delimiter);
                int idx2 = s.IndexOf('\"');
                if (idx >= 0 || idx2 >= 0) {
                    sb.Append('\"');
                    if (idx2 >= 0)
                        sb.Append( s.Replace("\"", "\"\"") );
                    else
                        sb.Append(s);
                    sb.Append('\"');
                } else
                    sb.Append(s);
                sb.Append(delimiter);
            }
            if (sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            return sb.ToString();
            //sb.Append("\r\n");
        }

        public void Dispose() {
            writer.Dispose();
        }
    }

}
