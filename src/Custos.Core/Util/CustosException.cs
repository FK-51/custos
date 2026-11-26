namespace Custos.Core.Util;

/// <summary>
/// 利用者に見せる失敗。メッセージは、そのまま表示して原因が分かる文にする。
/// </summary>
public class CustosException : Exception
{
    /// <summary>空のメッセージで作る。</summary>
    public CustosException()
    {
    }

    /// <summary>メッセージを付けて作る。</summary>
    public CustosException(string message)
        : base(message)
    {
    }

    /// <summary>原因の例外を付けて作る。</summary>
    public CustosException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
