using System.Drawing;

namespace WindowsLoop;

/// <summary>
/// 一个可以被推、会掉下去的箱子。
///
/// 数据上它只是地图 solid 层里的一个 '3'（<see cref="Tileset.SolidKind.Crate"/>），
/// 读关卡时被抽成实体、那一位随即清成「跟随瓦片」——之后箱子的位置由这里自己管，
/// 静态图层不会再多挡一次。
///
/// 位置有兩套坐标，别混用：
///   <see cref="CellX"/> / <see cref="CellY"/> —— 逻辑格（整数，阻挡判定和压力板都用它）
///   <see cref="X"/> / <see cref="Y"/>         —— 画在哪（格子左上角，连续；推箱补间和下落时才会离开整数）
/// 静止时两者重合，落点永远吸附在整数格上。
/// </summary>
internal sealed class Crate
{
    public Piece Piece = null!;

    public int CellX, CellY;
    public float X, Y;

    /// <summary>下落速度（格/秒），只在 <see cref="Falling"/> 时有意义。</summary>
    public float VY;

    // ---- 推箱补间 ----
    public bool Sliding;
    public float SlideT;
    public float FromX, FromY;
    public int ToX, ToY;

    public bool Falling;

    /// <summary>刚被推到位 / 刚落地时的压扁量（0..1），只影响绘制，用来做那一记"顿"。</summary>
    public float Impact;

    public Crate(Piece piece, int x, int y)
    {
        Piece = piece;
        CellX = ToX = x;
        CellY = ToY = y;
        X = x;
        Y = y;
    }

    /// <summary>脚底那一格：箱子站在哪一格上（压力板按这一格也认）。</summary>
    public int FeetY => CellY + 1;

    /// <summary>正在被推 / 正在掉：这期间不接受新的推动。</summary>
    public bool Busy => Sliding || Falling;

    /// <summary>
    /// 开始推一格的补间。**逻辑格立刻挪到目标格**（先把落点占住）：
    /// 补间期间别的东西（玩家、别的箱子、压力板）看到的都是"它已经在那边了"，
    /// 视觉位置再由 X / Y 慢慢追上去。这样"推到一半"也不会出现两个箱子抢同一格。
    /// </summary>
    public void BeginSlide(int tx, int ty)
    {
        FromX = X;
        FromY = Y;
        ToX = tx;
        ToY = ty;
        CellX = tx;
        CellY = ty;
        Sliding = true;
        SlideT = 0f;
    }

    /// <summary>补间结束：视觉位置落到目标整数格（逻辑格在 BeginSlide 时就已经挪好了）。</summary>
    public void CommitCell()
    {
        X = ToX;
        Y = ToY;
        Sliding = false;
    }
}
