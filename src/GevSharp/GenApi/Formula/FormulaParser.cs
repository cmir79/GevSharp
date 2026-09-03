using System.Globalization;

namespace GevSharp.GenApi;

internal enum FormulaTok
{
    End,
    Int, Dbl, Ident,
    LParen, RParen, Comma, Question, Colon,
    Plus, Minus, Star, Slash, Percent, StarStar,
    Amp, Pipe, Caret, Tilde, Bang,
    Shl, Shr, AmpAmp, PipePipe,
    Lt, Le, Gt, Ge, Eq, Ne,
}

/// <summary>
/// 수식 문자열을 트리로 만든다 — 손으로 쓴 어휘 분석기 + 우선순위 오르기 파서.
/// 좌결합 이항 연산은 반복으로 처리해 긴 체인이 재귀를 키우지 않고, 괄호·단항·함수 인자·삼항 가지·거듭제곱 지수처럼
/// 실제로 중첩되는 자리마다 깊이를 세어 <see cref="Formula.MaxDepth"/> 를 넘으면 예외를 낸다(스택 오버플로 방지).
/// 만들어진 트리의 깊이도 같은 상한으로 검사해 평가 재귀도 함께 묶는다.
/// </summary>
internal sealed class FormulaParser
{
    private readonly string _text;
    private int _pos;              // 다음에 읽을 문자
    private int _nesting;

    private FormulaTok _tok;
    private int _tokPos;           // 현재 토큰의 시작
    private string _tokText = "";  // 식별자 본문
    private long _tokInt;
    private double _tokDbl;

    private readonly List<string> _vars = new();
    private readonly Dictionary<string, int> _varIndex = new(StringComparer.Ordinal);

    private FormulaParser(string text)
    {
        _text = text;
    }

    /// <summary>파싱 진입점. variables 는 첫 등장 순서의 서로 다른 변수 이름.</summary>
    public static FormulaNode Parse(string text, out string[] variables)
    {
        var p = new FormulaParser(text);
        p.Next();
        if (p._tok == FormulaTok.End) throw p.Fail(0, "Formula is empty");
        var root = p.ParseTernary();
        if (p._tok != FormulaTok.End) throw p.Fail(p._tokPos, "Unexpected token '" + p.TokText() + "'");
        variables = p._vars.ToArray();
        return root;
    }

    // ---- 문법 ----

    private FormulaNode ParseTernary()
    {
        var cond = ParseBinary(1);
        if (_tok != FormulaTok.Question) return cond;

        int pos = _tokPos;
        Next();
        Enter(pos);
        var then = ParseTernary();
        Exit();
        if (_tok != FormulaTok.Colon) throw Fail(_tokPos, "Expected ':' in conditional expression");
        Next();
        Enter(pos);
        var @else = ParseTernary();   // 우결합: a ? b : c ? d : e == a ? b : (c ? d : e)
        Exit();
        return Check(new FormulaCondNode(pos, cond, then, @else));
    }

    /// <summary>이항 연산(모두 좌결합). 우선순위가 minPrec 이상인 연산만 여기서 묶는다.</summary>
    private FormulaNode ParseBinary(int minPrec)
    {
        var left = ParsePower();
        while (TryBinaryOp(_tok, out var op, out int prec) && prec >= minPrec)
        {
            int pos = _tokPos;
            Next();
            var right = ParseBinary(prec + 1);
            left = Check(new FormulaBinaryNode(pos, op, left, right));
        }
        return left;
    }

    private FormulaNode ParseUnary()
    {
        FormulaUnOp op;
        switch (_tok)
        {
            case FormulaTok.Plus:
            {
                // 단항 + 는 항등 — 노드를 만들지 않는다
                int p = _tokPos;
                Next();
                Enter(p);
                var operand = ParseUnary();
                Exit();
                return operand;
            }
            case FormulaTok.Minus: op = FormulaUnOp.Neg; break;
            case FormulaTok.Bang: op = FormulaUnOp.LogNot; break;
            case FormulaTok.Tilde: op = FormulaUnOp.BitNot; break;
            default: return ParsePrimary();
        }

        int pos = _tokPos;
        Next();
        Enter(pos);
        var inner = ParseUnary();
        Exit();
        return Check(new FormulaUnaryNode(pos, op, inner));
    }

    /// <summary>
    /// 거듭제곱은 단항 다음으로 세고 우결합이다 — 단항이 밑에 먼저 붙어 -2**2 = (-2)**2 = 4, ~2**2 = 9, 2**3**2 = 2**(3**2) = 512.
    /// 지수 자리도 단항으로 시작하므로 2**-1 처럼 부호를 붙일 수 있다(2**-2**2 = 2**((-2)**2) = 16).
    /// </summary>
    private FormulaNode ParsePower()
    {
        var b = ParseUnary();
        if (_tok != FormulaTok.StarStar) return b;

        int pos = _tokPos;
        Next();
        Enter(pos);
        var exponent = ParsePower();
        Exit();
        return Check(new FormulaBinaryNode(pos, FormulaBinOp.Pow, b, exponent));
    }

    private FormulaNode ParsePrimary()
    {
        switch (_tok)
        {
            case FormulaTok.Int:
            {
                var n = new FormulaConstNode(_tokPos, _tokInt);
                Next();
                return n;
            }
            case FormulaTok.Dbl:
            {
                var n = new FormulaConstNode(_tokPos, _tokDbl);
                Next();
                return n;
            }
            case FormulaTok.LParen:
            {
                int pos = _tokPos;
                Next();
                Enter(pos);
                var inner = ParseTernary();
                Exit();
                if (_tok != FormulaTok.RParen) throw Fail(_tokPos, "Expected ')' to close '(' at position " + pos.ToString(CultureInfo.InvariantCulture));
                Next();
                return inner;
            }
            case FormulaTok.Ident:
                return ParseIdent();
            case FormulaTok.End:
                throw Fail(_tokPos, "Unexpected end of formula");
            default:
                throw Fail(_tokPos, "Unexpected token '" + TokText() + "'");
        }
    }

    private FormulaNode ParseIdent()
    {
        string name = _tokText;
        int pos = _tokPos;
        Next();

        if (_tok == FormulaTok.LParen)
        {
            if (!TryFunc(name, out var func)) throw Fail(pos, "Unknown function '" + name + "'");
            Next();
            var args = new List<FormulaNode>(1);
            if (_tok != FormulaTok.RParen)
            {
                while (true)
                {
                    Enter(pos);
                    args.Add(ParseTernary());
                    Exit();
                    if (_tok != FormulaTok.Comma) break;
                    Next();
                }
            }
            if (_tok != FormulaTok.RParen) throw Fail(_tokPos, "Expected ')' after arguments of '" + name + "'");
            Next();
            // ROUND 만 소수 자릿수를 둘째 인자로 더 받을 수 있다(ROUND(x, 0)) — 나머지는 인자 하나.
            int maxArgs = func == FormulaFunc.Round ? 2 : 1;
            if (args.Count < 1 || args.Count > maxArgs)
                throw Fail(pos, "Function '" + name + "' expects " + (maxArgs == 2 ? "1 or 2 arguments" : "1 argument")
                    + ", got " + args.Count.ToString(CultureInfo.InvariantCulture));
            return Check(new FormulaFuncNode(pos, func, args[0], args.Count == 2 ? args[1] : null));
        }

        // 상수는 대문자 정확 일치만 — 소문자 e/pi 는 변수로 남겨 이름 충돌을 피한다
        if (name == "PI") return new FormulaConstNode(pos, Math.PI);
        if (name == "E") return new FormulaConstNode(pos, Math.E);

        if (!_varIndex.TryGetValue(name, out int index))
        {
            index = _vars.Count;
            _vars.Add(name);
            _varIndex.Add(name, index);
        }
        return new FormulaVarNode(pos, index, name);
    }

    private static bool TryBinaryOp(FormulaTok tok, out FormulaBinOp op, out int prec)
    {
        switch (tok)
        {
            case FormulaTok.PipePipe: op = FormulaBinOp.LogOr; prec = 1; return true;
            case FormulaTok.AmpAmp: op = FormulaBinOp.LogAnd; prec = 2; return true;
            case FormulaTok.Pipe: op = FormulaBinOp.BitOr; prec = 3; return true;
            case FormulaTok.Caret: op = FormulaBinOp.BitXor; prec = 4; return true;
            case FormulaTok.Amp: op = FormulaBinOp.BitAnd; prec = 5; return true;
            case FormulaTok.Eq: op = FormulaBinOp.Eq; prec = 6; return true;
            case FormulaTok.Ne: op = FormulaBinOp.Ne; prec = 6; return true;
            case FormulaTok.Lt: op = FormulaBinOp.Lt; prec = 7; return true;
            case FormulaTok.Le: op = FormulaBinOp.Le; prec = 7; return true;
            case FormulaTok.Gt: op = FormulaBinOp.Gt; prec = 7; return true;
            case FormulaTok.Ge: op = FormulaBinOp.Ge; prec = 7; return true;
            case FormulaTok.Shl: op = FormulaBinOp.Shl; prec = 8; return true;
            case FormulaTok.Shr: op = FormulaBinOp.Shr; prec = 8; return true;
            case FormulaTok.Plus: op = FormulaBinOp.Add; prec = 9; return true;
            case FormulaTok.Minus: op = FormulaBinOp.Sub; prec = 9; return true;
            case FormulaTok.Star: op = FormulaBinOp.Mul; prec = 10; return true;
            case FormulaTok.Slash: op = FormulaBinOp.Div; prec = 10; return true;
            case FormulaTok.Percent: op = FormulaBinOp.Mod; prec = 10; return true;
            default: op = default; prec = 0; return false;
        }
    }

    private static bool TryFunc(string name, out FormulaFunc func)
    {
        switch (name.ToUpperInvariant())
        {
            case "SIN": func = FormulaFunc.Sin; return true;
            case "COS": func = FormulaFunc.Cos; return true;
            case "TAN": func = FormulaFunc.Tan; return true;
            case "ASIN": func = FormulaFunc.Asin; return true;
            case "ACOS": func = FormulaFunc.Acos; return true;
            case "ATAN": func = FormulaFunc.Atan; return true;
            case "ABS": func = FormulaFunc.Abs; return true;
            case "EXP": func = FormulaFunc.Exp; return true;
            case "LN": func = FormulaFunc.Ln; return true;
            case "LG": func = FormulaFunc.Lg; return true;
            case "SQRT": func = FormulaFunc.Sqrt; return true;
            case "TRUNC": func = FormulaFunc.Trunc; return true;
            case "FLOOR": func = FormulaFunc.Floor; return true;
            case "CEIL": func = FormulaFunc.Ceil; return true;
            case "ROUND": func = FormulaFunc.Round; return true;
            case "SGN": func = FormulaFunc.Sgn; return true;
            case "NEG": func = FormulaFunc.Neg; return true;
            default: func = default; return false;
        }
    }

    // ---- 깊이 제한 ----

    private void Enter(int pos)
    {
        if (++_nesting > Formula.MaxDepth) throw Fail(pos, DepthMessage);
    }

    private void Exit() => _nesting--;

    private FormulaNode Check(FormulaNode node)
    {
        if (node.Depth > Formula.MaxDepth) throw Fail(node.Pos, DepthMessage);
        return node;
    }

    private static string DepthMessage
        => "Formula nesting exceeds the limit of " + Formula.MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels";

    private GenApiException Fail(int pos, string message) => new FormulaErrSite(_text, pos).Fail(message);

    private string TokText() => _text.Substring(_tokPos, _pos - _tokPos);

    // ---- 어휘 분석 ----

    private void Next()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        _tokPos = _pos;
        if (_pos >= _text.Length)
        {
            _tok = FormulaTok.End;
            return;
        }

        char c = _text[_pos];
        char n = _pos + 1 < _text.Length ? _text[_pos + 1] : '\0';

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(n)))
        {
            LexNumber();
            return;
        }
        if (char.IsLetter(c) || c == '_')
        {
            LexIdent();
            return;
        }

        switch (c)
        {
            case '(': Set(FormulaTok.LParen, 1); return;
            case ')': Set(FormulaTok.RParen, 1); return;
            case ',': Set(FormulaTok.Comma, 1); return;
            case '?': Set(FormulaTok.Question, 1); return;
            case ':': Set(FormulaTok.Colon, 1); return;
            case '+': Set(FormulaTok.Plus, 1); return;
            case '-': Set(FormulaTok.Minus, 1); return;
            case '*': if (n == '*') Set(FormulaTok.StarStar, 2); else Set(FormulaTok.Star, 1); return;
            case '/': Set(FormulaTok.Slash, 1); return;
            case '%': Set(FormulaTok.Percent, 1); return;
            case '~': Set(FormulaTok.Tilde, 1); return;
            case '^': Set(FormulaTok.Caret, 1); return;
            case '&': if (n == '&') Set(FormulaTok.AmpAmp, 2); else Set(FormulaTok.Amp, 1); return;
            case '|': if (n == '|') Set(FormulaTok.PipePipe, 2); else Set(FormulaTok.Pipe, 1); return;
            case '!': if (n == '=') Set(FormulaTok.Ne, 2); else Set(FormulaTok.Bang, 1); return;
            case '=': if (n == '=') Set(FormulaTok.Eq, 2); else Set(FormulaTok.Eq, 1); return;
            case '<':
                if (n == '<') Set(FormulaTok.Shl, 2);
                else if (n == '=') Set(FormulaTok.Le, 2);
                else if (n == '>') Set(FormulaTok.Ne, 2);
                else Set(FormulaTok.Lt, 1);
                return;
            case '>':
                if (n == '>') Set(FormulaTok.Shr, 2);
                else if (n == '=') Set(FormulaTok.Ge, 2);
                else Set(FormulaTok.Gt, 1);
                return;
            default:
                throw Fail(_pos, "Unexpected character '" + c + "'");
        }
    }

    private void Set(FormulaTok tok, int length)
    {
        _tok = tok;
        _pos += length;
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    /// <summary>
    /// 숫자 리터럴. 0x 접두는 16진 정수(16자리까지, 최상위 비트가 서면 음수로 재해석 — 0xFFFFFFFFFFFFFFFF = -1).
    /// 소수점이나 지수가 있으면 실수, 아니면 10진 정수. 정수의 64비트 범위 밖과 실수의 double 범위 밖(1e400)은 예외 —
    /// 범위 밖 실수는 런타임에 따라 파싱 실패와 무한대로 갈리므로 여기서 한 가지(오류)로 정한다.
    /// </summary>
    private void LexNumber()
    {
        int start = _pos;
        int len = _text.Length;
        char c = _text[_pos];
        char n = _pos + 1 < len ? _text[_pos + 1] : '\0';

        if (c == '0' && (n == 'x' || n == 'X'))
        {
            _pos += 2;
            int digitsStart = _pos;
            while (_pos < len && IsHexDigit(_text[_pos])) _pos++;
            int digits = _pos - digitsStart;
            if (digits == 0) throw Fail(start, "Hexadecimal literal has no digits");
            if (digits > 16) throw Fail(start, "Hexadecimal literal exceeds 64 bits");
            ulong u = ulong.Parse(_text.Substring(digitsStart, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            _tokInt = unchecked((long)u);
            _tok = FormulaTok.Int;
        }
        else
        {
            bool isFloat = false;
            while (_pos < len && char.IsDigit(_text[_pos])) _pos++;
            if (_pos < len && _text[_pos] == '.')
            {
                isFloat = true;
                _pos++;
                while (_pos < len && char.IsDigit(_text[_pos])) _pos++;
            }
            if (_pos < len && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (_pos < len && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                if (_pos >= len || !char.IsDigit(_text[_pos])) throw Fail(start, "Invalid number literal '" + _text.Substring(start, _pos - start) + "'");
                isFloat = true;
                while (_pos < len && char.IsDigit(_text[_pos])) _pos++;
            }

            string s = _text.Substring(start, _pos - start);
            if (isFloat)
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _tokDbl) || double.IsInfinity(_tokDbl))
                    throw Fail(start, "Floating-point literal '" + s + "' is outside the range of a double");
                _tok = FormulaTok.Dbl;
            }
            else
            {
                if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _tokInt))
                    throw Fail(start, "Integer literal '" + s + "' is outside the 64-bit range");
                _tok = FormulaTok.Int;
            }
        }

        // 숫자에 식별자 문자가 바로 붙으면(12abc, 0x1G) 리터럴이 아니다
        if (_pos < len && IsIdentChar(_text[_pos]))
            throw Fail(start, "Invalid number literal '" + _text.Substring(start, _pos - start + 1) + "'");
    }

    /// <summary>식별자: 글자 또는 '_' 로 시작해 글자·숫자·'_'·'.' 가 이어진다.</summary>
    private void LexIdent()
    {
        int start = _pos;
        while (_pos < _text.Length && IsIdentChar(_text[_pos])) _pos++;
        _tokText = _text.Substring(start, _pos - start);
        _tok = FormulaTok.Ident;
    }
}
