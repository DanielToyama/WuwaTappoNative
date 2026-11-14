using System;

namespace WuwatappoNative.Pet;

/// <summary>
/// 单个头饰的本地微调参数：X/Y 逻辑像素、缩放百分比、旋转角度。
/// 持久化在 Settings.AccessoryOffsets 的 int[] 中，兼容旧的 [x,y] 两元素数据。
/// </summary>
public readonly record struct AccessoryTransform(int X, int Y, int ScalePercent, int RotationDegrees)
{
    public const int MinOffset = -160;
    public const int MaxOffset = 160;
    public const int MinScalePercent = 25;
    public const int MaxScalePercent = 175;
    public const int MinRotationDegrees = -180;
    public const int MaxRotationDegrees = 180;

    public static AccessoryTransform Default => new(0, 0, 100, 0);

    /// <summary>
    /// 旧配置只有 [x,y]；缺省缩放 100%、旋转 0°。历史版本可能保存过 300% 等超出新对称范围
    /// （25–175，100 居中）的值，读取时统一收敛到上限，保证滑条与 native 显示一致且仍可归零。
    /// </summary>
    public static AccessoryTransform FromArray(int[] values)
    {
        if (values == null || values.Length < 2) return Default;
        int scale = values.Length >= 3 && values[2] > 0 ? values[2] : 100;
        int rotation = values.Length >= 4 ? values[3] : 0;
        return new AccessoryTransform(values[0], values[1], scale, rotation).Clamped();
    }

    public AccessoryTransform Clamped()
    {
        int scale = ScalePercent >= MinScalePercent && ScalePercent <= MaxScalePercent
            ? ScalePercent
            : Math.Clamp(ScalePercent <= 0 ? 100 : ScalePercent, MinScalePercent, MaxScalePercent);
        return new AccessoryTransform(
            Math.Clamp(X, MinOffset, MaxOffset),
            Math.Clamp(Y, MinOffset, MaxOffset),
            scale,
            Math.Clamp(RotationDegrees, MinRotationDegrees, MaxRotationDegrees));
    }

    public int[] ToArray() => new[] { X, Y, ScalePercent, RotationDegrees };
}
