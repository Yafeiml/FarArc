using Shawn.Utils;

namespace FarArc.View.Utils;

public abstract class MaskLayer : NotifyPropertyChangedBase
{
    public long LayerId { get; set; }

    /// <summary>
    /// 判断当前遮罩是否应当被移除
    /// </summary>
    /// <param name="latestLayerId"></param>
    /// <returns></returns>
    public bool IsNeedRemoval(long latestLayerId)
    {
        return latestLayerId >= LayerId;
    }
}