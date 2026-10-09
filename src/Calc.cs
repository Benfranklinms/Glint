using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Glint
{
    /// Inline answers: arithmetic ("18% of 4500", "(2+3)^2", "sqrt(2)") and
    /// unit conversion ("5 km in miles", "100 f to c", "2 gb in mb").
    public static class Calc
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Answer { public string Text; public string Value; public string Hint; }

        public static Answer TryAnswer(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string s = input.Trim();
            if (s.StartsWith("=")) s = s.Substring(1).Trim();
            if (s.Length == 0 || s.Length > 120) return null;
            try { return Units(s) ?? Percent(s) ?? Expression(s, input.TrimStart().StartsWith("=")); }
            catch { return null; }
        }

        // ---------- units ----------

        private static readonly Dictionary<string, (string dim, double f)> U = Build();

        private static Dictionary<string, (string, double)> Build()
        {
            var d = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
            void Add(string dim, double f, params string[] names) { foreach (var n in names) d[n] = (dim, f); }
            Add("len", 0.001, "mm", "millimeter", "millimetre"); Add("len", 0.01, "cm", "centimeter", "centimetre");
            Add("len", 1, "m", "meter", "metre"); Add("len", 1000, "km", "kilometer", "kilometre");
            Add("len", 0.0254, "in", "inch", "inches"); Add("len", 0.3048, "ft", "foot", "feet");
            Add("len", 0.9144, "yd", "yard"); Add("len", 1609.344, "mi", "mile"); Add("len", 1852, "nmi", "nautical mile");
            Add("mass", 1e-6, "mg", "milligram"); Add("mass", 0.001, "g", "gram", "gm"); Add("mass", 1, "kg", "kilogram", "kilo");
            Add("mass", 1000, "t", "tonne", "ton"); Add("mass", 0.028349523125, "oz", "ounce");
            Add("mass", 0.45359237, "lb", "lbs", "pound"); Add("mass", 6.35029318, "st", "stone");
            Add("vol", 0.001, "ml", "milliliter", "millilitre"); Add("vol", 1, "l", "liter", "litre");
            Add("vol", 3.785411784, "gal", "gallon"); Add("vol", 0.946352946, "qt", "quart"); Add("vol", 0.473176473, "pt", "pint");
            Add("vol", 0.2365882365, "cup"); Add("vol", 0.0295735295625, "floz", "fl oz", "fluid ounce");
            Add("vol", 0.00492892159375, "tsp", "teaspoon"); Add("vol", 0.01478676478125, "tbsp", "tablespoon");
            Add("data", 0.125, "bit"); Add("data", 1, "b", "byte"); Add("data", 1024, "kb", "kib", "kilobyte");
            Add("data", 1024d * 1024, "mb", "mib", "megabyte"); Add("data", 1024d * 1024 * 1024, "gb", "gib", "gigabyte");
            Add("data", Math.Pow(1024, 4), "tb", "tib", "terabyte");
            Add("time", 0.001, "ms", "millisecond"); Add("time", 1, "s", "sec", "second"); Add("time", 60, "min", "minute");
            Add("time", 3600, "h", "hr", "hour"); Add("time", 86400, "d", "day"); Add("time", 604800, "wk", "week");
            Add("time", 2629800, "month"); Add("time", 31557600, "yr", "year");
            Add("speed", 1, "m/s", "mps"); Add("speed", 1 / 3.6, "km/h", "kmh", "kph"); Add("speed", 0.44704, "mph");
            Add("speed", 0.514444, "kn", "knot");
            Add("area", 1, "sqm", "m2", "m²"); Add("area", 0.09290304, "sqft", "ft2", "ft²"); Add("area", 1e6, "sqkm", "km2", "km²");
            Add("area", 4046.8564224, "acre"); Add("area", 10000, "ha", "hectare");
            Add("temp", 0, "c", "celsius", "°c", "degc"); Add("temp", 0, "f", "fahrenheit", "°f", "degf"); Add("temp", 0, "k", "kelvin");
            return d;
        }

        private static bool Lookup(string raw, out string name, out (string dim, double f) u)
        {
            name = raw.Trim().ToLowerInvariant();
            if (U.TryGetValue(name, out u)) return true;
            if (name.EndsWith("es") && U.TryGetValue(name.Substring(0, name.Length - 2), out u)) return true;
            if (name.EndsWith("s") && U.TryGetValue(name.Substring(0, name.Length - 1), out u)) return true;
            return false;
        }

        private static readonly Regex UnitRx = new Regex(@"^(-?[\d.,]+)\s*([a-z°²/ ]+?)\s+(?:in|to|as|into)\s+([a-z°²/ ]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Answer Units(string s)
        {
            var m = UnitRx.Match(s);
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, Inv, out double v)) return null;
            if (!Lookup(m.Groups[2].Value, out string a, out var ua) || !Lookup(m.Groups[3].Value, out string b, out var ub)) return null;
            if (ua.dim != ub.dim) return null;
            double r;
            if (ua.dim == "temp")
            {
                char fa = Canon(a), fb = Canon(b);
                double c = fa == 'c' ? v : fa == 'f' ? (v - 32) * 5 / 9 : v - 273.15;
                r = fb == 'c' ? c : fb == 'f' ? c * 9 / 5 + 32 : c + 273.15;
            }
            else r = v * ua.f / ub.f;
            string unit = m.Groups[3].Value.Trim();
            string val = Format(r);
            return new Answer { Text = $"{val} {unit}", Value = val, Hint = $"{Format(v)} {m.Groups[2].Value.Trim()} = {val} {unit}" };
        }

        private static char Canon(string t) => t.Contains("f") ? 'f' : t.StartsWith("k") ? 'k' : 'c';

        // ---------- percentages ----------

        private static readonly Regex PctRx = new Regex(@"^(-?[\d.,]+)\s*%\s*(?:of)\s*(-?[\d.,]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Answer Percent(string s)
        {
            var m = PctRx.Match(s);
            if (!m.Success) return null;
            double p = double.Parse(m.Groups[1].Value.Replace(",", ""), Inv), x = double.Parse(m.Groups[2].Value.Replace(",", ""), Inv);
            string val = Format(p / 100 * x);
            return new Answer { Text = val, Value = val, Hint = $"{m.Groups[1].Value}% of {m.Groups[2].Value}" };
        }

        // ---------- expressions ----------

        private static readonly HashSet<string> Funcs = new HashSet<string> { "sqrt", "sin", "cos", "tan", "asin", "acos", "atan", "log", "ln", "abs", "round", "floor", "ceil", "exp" };

        private static Answer Expression(string s, bool forced)
        {
            string e = s.Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("**", "^").Replace(",", "");
            // only answer things that look like maths: digits plus an operator or a function
            bool hasDigit = e.Any(char.IsDigit) || e.Contains("pi");
            bool hasOp = Regex.IsMatch(e, @"(?:[\d)\s]|pi|\be)[+\-*/^%x]\s*[\d(.a-z]|^\s*-?\s*[a-z]+\s*\(", RegexOptions.IgnoreCase);
            if (!forced && (!hasDigit || !hasOp)) return null;
            if (!forced && Regex.IsMatch(e.Trim(), @"^\d+([-/.]\d+){2,}$")) return null; // dates, versions, phone numbers
            if (!forced && Regex.IsMatch(e, @"[a-wyz]", RegexOptions.IgnoreCase) && !Regex.IsMatch(e, @"^[\d\s+\-*/^%().x]*(?:(?:sqrt|sin|cos|tan|asin|acos|atan|log|ln|abs|round|floor|ceil|exp|pi|e)[\d\s+\-*/^%().x]*)+$", RegexOptions.IgnoreCase))
                return null;
            e = Regex.Replace(e, @"(?<=[\d)])\s*x\s*(?=[\d(])", "*", RegexOptions.IgnoreCase);
            var p = new Parser(e);
            double v = p.ParseExpr();
            p.SkipWs();
            if (!p.AtEnd || double.IsNaN(v) || double.IsInfinity(v)) return null;
            string val = Format(v);
            return new Answer { Text = val, Value = val, Hint = s };
        }

        public static string Format(double v)
        {
            if (Math.Abs(v) < 1e15 && Math.Abs(v - Math.Round(v)) < 1e-9) return Math.Round(v).ToString("#,0", Inv);
            if (Math.Abs(v) >= 1e15 || Math.Abs(v) < 1e-6) return v.ToString("G8", Inv);
            double r = Math.Round(v, Math.Max(0, 9 - (int)Math.Floor(Math.Log10(Math.Abs(v))) ));
            string t = r.ToString("#,0.##########", Inv);
            return t;
        }

        private sealed class Parser
        {
            private readonly string s; private int i;
            public Parser(string s) { this.s = s; }
            public bool AtEnd => i >= s.Length;
            public void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            private bool Eat(char c) { SkipWs(); if (i < s.Length && s[i] == c) { i++; return true; } return false; }

            public double ParseExpr()
            {
                double v = ParseTerm();
                while (true)
                {
                    if (Eat('+')) v += ParseTerm();
                    else if (Eat('-')) v -= ParseTerm();
                    else return v;
                }
            }

            private double ParseTerm()
            {
                double v = ParseUnary();
                while (true)
                {
                    if (Eat('*')) v *= ParseUnary();
                    else if (Eat('/')) v /= ParseUnary();
                    else if (Eat('%'))
                    {
                        // "50 % 7" is modulo; a trailing "%" means percent
                        SkipWs();
                        if (i < s.Length && (char.IsDigit(s[i]) || s[i] == '(' || s[i] == '.')) v %= ParseUnary();
                        else v /= 100;
                    }
                    else return v;
                }
            }

            private double ParseUnary()
            {
                if (Eat('-')) return -ParseUnary();
                if (Eat('+')) return ParseUnary();
                return ParsePow();
            }

            private double ParsePow()
            {
                double b = ParseAtom();
                if (Eat('^')) return Math.Pow(b, ParseUnary());
                return b;
            }

            private double ParseAtom()
            {
                SkipWs();
                if (Eat('(')) { double v = ParseExpr(); if (!Eat(')')) throw new FormatException(); return v; }
                int st = i;
                if (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                {
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-' || s[i + 1] == '+'))
                    { i += 2; while (i < s.Length && char.IsDigit(s[i])) i++; }
                    return double.Parse(s.Substring(st, i - st), Inv);
                }
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string w = s.Substring(st, i - st).ToLowerInvariant();
                if (w == "pi") return Math.PI;
                if (w == "e") return Math.E;
                if (Funcs.Contains(w))
                {
                    double a = ParseAtomOrParen();
                    switch (w)
                    {
                        case "sqrt": return Math.Sqrt(a);
                        case "sin": return Math.Sin(a); case "cos": return Math.Cos(a); case "tan": return Math.Tan(a);
                        case "asin": return Math.Asin(a); case "acos": return Math.Acos(a); case "atan": return Math.Atan(a);
                        case "log": return Math.Log10(a); case "ln": return Math.Log(a); case "abs": return Math.Abs(a);
                        case "round": return Math.Round(a); case "floor": return Math.Floor(a); case "ceil": return Math.Ceiling(a);
                        case "exp": return Math.Exp(a);
                    }
                }
                throw new FormatException();
            }

            private double ParseAtomOrParen() => ParsePow();
        }
    }
}
