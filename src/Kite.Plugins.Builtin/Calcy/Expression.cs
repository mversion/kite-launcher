using System.Globalization;

namespace Kite.Plugins.Builtin.Calcy;

/// <summary>
/// Small recursive-descent calculator: + - * / % ^ ! parentheses, implicit "x"/"×" multiply,
/// constants (pi, e) and functions (sqrt, abs, sin, cos, tan, asin, acos, atan, log, ln, exp,
/// round, floor, ceil, min, max, pow). Trig works in degrees, matching Launchy's Calcy.
/// </summary>
public sealed class Expression
{
    private readonly string _s;
    private readonly char _decimal;
    private readonly char _argSeparator;
    private int _pos;

    private Expression(string s, bool commaDecimal)
    {
        _s = s;
        _decimal = commaDecimal ? ',' : '.';
        _argSeparator = commaDecimal ? ';' : ',';
    }

    public static bool TryEvaluate(string text, bool commaDecimal, out double value)
    {
        value = 0;
        try
        {
            var e = new Expression(text.Trim().TrimStart('='), commaDecimal);
            value = e.ParseExpression();
            e.SkipSpaces();
            return e._pos == e._s.Length && !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (FormatException) { return false; }
    }

    private double ParseExpression()
    {
        var v = ParseTerm();
        while (true)
        {
            if (Accept('+')) v += ParseTerm();
            else if (Accept('-')) v -= ParseTerm();
            else return v;
        }
    }

    private double ParseTerm()
    {
        var v = ParseUnary();
        while (true)
        {
            if (Accept('*') || Accept('×') || AcceptTimesX()) v *= ParseUnary();
            else if (Accept('/') || Accept('÷')) v /= ParseUnary();
            else if (Accept('%')) v %= ParseUnary();
            else if (Peek() == '(') v *= ParseUnary(); // 2(3+4)
            else return v;
        }
    }

    /// <summary>Unary minus binds looser than ^, so -3^2 = -9 (standard maths convention).</summary>
    private double ParseUnary()
    {
        if (Accept('-')) return -ParseUnary();
        if (Accept('+')) return ParseUnary();
        return ParsePower();
    }

    private double ParsePower()
    {
        var b = ParsePostfix();
        if (Accept('^')) return Math.Pow(b, ParseUnary()); // right-associative, allows 2^-1
        return b;
    }

    private double ParsePostfix()
    {
        var v = ParsePrimary();
        while (Accept('!')) v = Factorial(v);
        return v;
    }

    private double ParsePrimary()
    {
        SkipSpaces();
        if (Accept('('))
        {
            var v = ParseExpression();
            Expect(')');
            return v;
        }

        var c = Peek();
        if (char.IsDigit(c) || c == _decimal) return ParseNumber();
        if (char.IsLetter(c)) return ParseIdentifier();
        throw new FormatException();
    }

    private double ParseNumber()
    {
        var start = _pos;
        while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == _decimal)) _pos++;
        if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E') && _pos + 1 < _s.Length &&
            (char.IsDigit(_s[_pos + 1]) || ((_s[_pos + 1] == '-' || _s[_pos + 1] == '+') && _pos + 2 < _s.Length && char.IsDigit(_s[_pos + 2]))))
        {
            _pos += 2;
            while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
        }
        var token = _s[start.._pos].Replace(_decimal, '.');
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new FormatException();
        return v;
    }

    private double ParseIdentifier()
    {
        var start = _pos;
        while (_pos < _s.Length && char.IsLetterOrDigit(_s[_pos])) _pos++;
        var name = _s[start.._pos].ToLowerInvariant();

        switch (name)
        {
            case "pi": return Math.PI;
            case "e": return Math.E;
        }

        var args = new List<double>();
        Expect('(');
        if (!Accept(')'))
        {
            do args.Add(ParseExpression());
            while (Accept(_argSeparator));
            Expect(')');
        }

        double Arg(int i) => i < args.Count ? args[i] : throw new FormatException();
        const double Deg = Math.PI / 180;
        return name switch
        {
            "sqrt" => Math.Sqrt(Arg(0)),
            "abs" => Math.Abs(Arg(0)),
            "sin" => Math.Sin(Arg(0) * Deg),
            "cos" => Math.Cos(Arg(0) * Deg),
            "tan" => Math.Tan(Arg(0) * Deg),
            "asin" => Math.Asin(Arg(0)) / Deg,
            "acos" => Math.Acos(Arg(0)) / Deg,
            "atan" => Math.Atan(Arg(0)) / Deg,
            "log" => args.Count > 1 ? Math.Log(Arg(0), Arg(1)) : Math.Log10(Arg(0)),
            "ln" => Math.Log(Arg(0)),
            "exp" => Math.Exp(Arg(0)),
            "round" => Math.Round(Arg(0), args.Count > 1 ? (int)Arg(1) : 0, MidpointRounding.AwayFromZero),
            "floor" => Math.Floor(Arg(0)),
            "ceil" => Math.Ceiling(Arg(0)),
            "min" => args.Count > 0 ? args.Min() : throw new FormatException(),
            "max" => args.Count > 0 ? args.Max() : throw new FormatException(),
            "pow" => Math.Pow(Arg(0), Arg(1)),
            _ => throw new FormatException(),
        };
    }

    private static double Factorial(double v)
    {
        if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException();
        double r = 1;
        for (var i = 2; i <= (int)v; i++) r *= i;
        return r;
    }

    /// <summary>"5x7" - an 'x' between operands means multiply (but not inside names like "exp").</summary>
    private bool AcceptTimesX()
    {
        SkipSpaces();
        if (_pos < _s.Length && (_s[_pos] == 'x' || _s[_pos] == 'X') &&
            _pos + 1 < _s.Length && (char.IsDigit(_s[_pos + 1]) || _s[_pos + 1] == '(' || _s[_pos + 1] == ' ' || _s[_pos + 1] == _decimal))
        {
            _pos++;
            return true;
        }
        return false;
    }

    private char Peek()
    {
        SkipSpaces();
        return _pos < _s.Length ? _s[_pos] : '\0';
    }

    private bool Accept(char c)
    {
        SkipSpaces();
        if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; }
        return false;
    }

    private void Expect(char c)
    {
        if (!Accept(c)) throw new FormatException();
    }

    private void SkipSpaces()
    {
        while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
    }
}
