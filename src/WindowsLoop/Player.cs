using System.Numerics;

namespace WindowsLoop;

internal sealed class Player
{
    public const float Width = 0.62f;
    public const float Height = 0.9f;

    public const float Speed = 10f;

    /// <summary>
    /// 跳跃参数取自原版 Player.prefab（JumpHeight 2.5 / JumpTime 0.35 / FallGravityScale 1.8）。
    /// 注意标称高度和实际顶点不等：离散积分下 2.5 的实际顶点约 2.27 格，
    /// 与"原版大概 2.2 格"的手感一致（之前标称 3.0 实测能上 2.81，明显偏高）。
    /// </summary>
    public const float JumpHeight = 2.5f;
    public const float JumpTime = 0.35f;

    /// <summary>下落时的重力倍率：落得比升得快，原版是 1.8。</summary>
    public const float FallGravityScale = 1.8f;

    /// <summary>
    /// 下坠速度上限（格/秒）。不给上限的话，20 行的图掉到底会到 56 格/秒，
    /// 相机再怎么跟也会被甩开（滞后 ≈ 速度 × 0.22 秒），画面糊成一片。
    /// </summary>
    public const float MaxFallSpeed = 30f;

    public const float CoyoteTime = 0.1f;
    public const float JumpBuffer = 0.2f;
    public const float ClimbSpeed = 6f;          // 爬梯子：格/秒
    public const float SpringHeight = 6f;        // 弹簧能弹多高（格）
    public const float LadderJumpLock = 0.22f;   // 从梯子上跳开后，这段时间内不再被梯子抓住

    public Vector2 Pos;   // 所在那块地图的坐标（格），不是屏幕坐标
    public Vector2 Vel;
    public Piece Piece = null!;   // 当前所在的地图片段
    public bool OnGround;
    public bool FacingRight = true;
    public float Coyote;
    public float Buffer;

    /// <summary>上下输入（-1 上 / +1 下），爬梯子用。</summary>
    public float ClimbAxis;
    public float LadderLock;

    public static float Gravity(float dt)
    {
        float n = JumpTime / dt;
        return 2f * JumpHeight / (n * (n + 1f) * dt * dt);
    }

    public static float JumpVelocity(float dt) => Gravity(dt) * JumpTime;

    /// <summary>要弹到 height 那么高，需要的初速度。</summary>
    public static float VelocityFor(float height, float dt) => MathF.Sqrt(2f * Gravity(dt) * height);
}
