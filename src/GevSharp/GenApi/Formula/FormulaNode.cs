namespace GevSharp.GenApi;

internal enum FormulaBinOp
{
    Add, Sub, Mul, Div, Mod, Pow,
    BitAnd, BitOr, BitXor, Shl, Shr,
    LogAnd, LogOr,
    Eq, Ne, Lt, Le, Gt, Ge,
}

internal enum FormulaUnOp
{
    Neg, LogNot, BitNot,
}

internal enum FormulaFunc
{
    Sin, Cos, Tan, Asin, Acos, Atan,
    Abs, Exp, Ln, Lg, Sqrt,
    Trunc, Floor, Ceil, Round, Sgn, Neg,
}

/// <summary>
/// 한 번의 평가 동안 살아 있는 상태 — 변수 값 슬롯과 지연 해석기. 변수는 첫 사용 때 한 번만 해석되고,
/// 택하지 않은 삼항 가지의 변수는 해석하지 않는다(장치 읽기를 아낀다). 해석기가 없으면 미해석 변수는 예외.
/// </summary>
internal sealed class FormulaEvalCtx
{
    private readonly string _text;
    private readonly string[] _names;
    private readonly GenApiValue[] _values;
    private readonly bool[] _isResolved;
    private readonly Func<string, GenApiValue>? _resolve;

    public FormulaEvalCtx(string text, string[] names, Func<string, GenApiValue>? resolve)
    {
        _text = text;
        _names = names;
        _values = names.Length == 0 ? Array.Empty<GenApiValue>() : new GenApiValue[names.Length];
        _isResolved = names.Length == 0 ? Array.Empty<bool>() : new bool[names.Length];
        _resolve = resolve;
    }

    public FormulaErrSite Site(int pos) => new(_text, pos);

    public void SetVariable(int index, GenApiValue value)
    {
        _values[index] = value;
        _isResolved[index] = true;
    }

    public GenApiValue GetVariable(int index, int pos)
    {
        if (_isResolved[index]) return _values[index];
        if (_resolve is null) throw Site(pos).Fail("Unknown variable '" + _names[index] + "'");
        var v = _resolve(_names[index]);
        _values[index] = v;
        _isResolved[index] = true;
        return v;
    }
}

/// <summary>수식 트리 노드. 불변이며 평가 상태는 전부 <see cref="FormulaEvalCtx"/> 에 있다.</summary>
internal abstract class FormulaNode
{
    /// <summary>원문에서의 0 기준 위치 — 오류 메시지용.</summary>
    public int Pos { get; }

    /// <summary>이 노드를 뿌리로 한 트리 깊이(잎 = 1). 평가 재귀 깊이의 상한이라 파서가 제한한다.</summary>
    public int Depth { get; }

    protected FormulaNode(int pos, int depth)
    {
        Pos = pos;
        Depth = depth;
    }

    public abstract GenApiValue Eval(FormulaEvalCtx ctx);
}

internal sealed class FormulaConstNode : FormulaNode
{
    private readonly GenApiValue _value;

    public FormulaConstNode(int pos, GenApiValue value) : base(pos, 1)
    {
        _value = value;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx) => _value;
}

internal sealed class FormulaVarNode : FormulaNode
{
    private readonly int _index;

    public string Name { get; }

    public FormulaVarNode(int pos, int index, string name) : base(pos, 1)
    {
        _index = index;
        Name = name;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx) => ctx.GetVariable(_index, Pos);
}

internal sealed class FormulaUnaryNode : FormulaNode
{
    private readonly FormulaUnOp _op;
    private readonly FormulaNode _operand;

    public FormulaUnaryNode(int pos, FormulaUnOp op, FormulaNode operand) : base(pos, operand.Depth + 1)
    {
        _op = op;
        _operand = operand;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx)
    {
        var v = _operand.Eval(ctx);
        return _op switch
        {
            FormulaUnOp.Neg => FormulaOps.Negate(v, ctx.Site(Pos)),
            FormulaUnOp.LogNot => GenApiValue.FromBoolean(!v.IsNonZero),
            FormulaUnOp.BitNot => FormulaOps.BitNot(v, ctx.Site(Pos)),
            _ => throw new ArgumentOutOfRangeException(nameof(_op)),
        };
    }
}

internal sealed class FormulaBinaryNode : FormulaNode
{
    private readonly FormulaBinOp _op;
    private readonly FormulaNode _left;
    private readonly FormulaNode _right;

    public FormulaBinaryNode(int pos, FormulaBinOp op, FormulaNode left, FormulaNode right)
        : base(pos, Math.Max(left.Depth, right.Depth) + 1)
    {
        _op = op;
        _left = left;
        _right = right;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx)
    {
        // 논리 연산은 단락 평가 — 왼쪽으로 결과가 정해지면 오른쪽은 건드리지 않는다.
        switch (_op)
        {
            case FormulaBinOp.LogAnd:
                return GenApiValue.FromBoolean(_left.Eval(ctx).IsNonZero && _right.Eval(ctx).IsNonZero);
            case FormulaBinOp.LogOr:
                return GenApiValue.FromBoolean(_left.Eval(ctx).IsNonZero || _right.Eval(ctx).IsNonZero);
        }

        var l = _left.Eval(ctx);
        var r = _right.Eval(ctx);
        var site = ctx.Site(Pos);
        return _op switch
        {
            FormulaBinOp.Add => FormulaOps.Add(l, r, site),
            FormulaBinOp.Sub => FormulaOps.Subtract(l, r, site),
            FormulaBinOp.Mul => FormulaOps.Multiply(l, r, site),
            FormulaBinOp.Div => FormulaOps.Divide(l, r, site),
            FormulaBinOp.Mod => FormulaOps.Modulo(l, r, site),
            FormulaBinOp.Pow => FormulaOps.Pow(l, r, site),
            FormulaBinOp.BitAnd => FormulaOps.BitAnd(l, r, site),
            FormulaBinOp.BitOr => FormulaOps.BitOr(l, r, site),
            FormulaBinOp.BitXor => FormulaOps.BitXor(l, r, site),
            FormulaBinOp.Shl => FormulaOps.ShiftLeft(l, r, site),
            FormulaBinOp.Shr => FormulaOps.ShiftRight(l, r, site),
            FormulaBinOp.Eq or FormulaBinOp.Ne or FormulaBinOp.Lt or FormulaBinOp.Le or FormulaBinOp.Gt or FormulaBinOp.Ge
                => FormulaOps.Compare(_op, l, r),
            _ => throw new ArgumentOutOfRangeException(nameof(_op)),
        };
    }
}

/// <summary>삼항 조건 — 조건을 먼저 평가하고 택한 가지만 평가한다. 택하지 않은 가지의 오류·변수는 건드리지 않는다.</summary>
internal sealed class FormulaCondNode : FormulaNode
{
    private readonly FormulaNode _cond;
    private readonly FormulaNode _then;
    private readonly FormulaNode _else;

    public FormulaCondNode(int pos, FormulaNode cond, FormulaNode then, FormulaNode @else)
        : base(pos, Math.Max(cond.Depth, Math.Max(then.Depth, @else.Depth)) + 1)
    {
        _cond = cond;
        _then = then;
        _else = @else;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx)
        => _cond.Eval(ctx).IsNonZero ? _then.Eval(ctx) : _else.Eval(ctx);
}

/// <summary>함수 호출 — 인자 하나. ROUND 는 소수 자릿수를 둘째 인자로 더 받을 수 있다(없으면 null).</summary>
internal sealed class FormulaFuncNode : FormulaNode
{
    private readonly FormulaFunc _func;
    private readonly FormulaNode _arg;
    private readonly FormulaNode? _arg2;

    public FormulaFuncNode(int pos, FormulaFunc func, FormulaNode arg, FormulaNode? arg2 = null)
        : base(pos, Math.Max(arg.Depth, arg2?.Depth ?? 0) + 1)
    {
        _func = func;
        _arg = arg;
        _arg2 = arg2;
    }

    public override GenApiValue Eval(FormulaEvalCtx ctx)
    {
        var x = _arg.Eval(ctx);
        return _arg2 is null
            ? FormulaOps.Call(_func, x, ctx.Site(Pos))
            : FormulaOps.Call(_func, x, _arg2.Eval(ctx), ctx.Site(Pos));
    }
}
