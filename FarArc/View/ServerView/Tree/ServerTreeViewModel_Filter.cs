using FarArc.View.ServerView;

namespace FarArc.View.ServerView.Tree
{
    public partial class ServerTreeViewModel : ServerPageViewModelBase
    {
        public sealed override void CalcServerVisibleAndRefresh(bool force = false, bool matchSubTitle = true)
        {
            base.CalcServerVisibleAndRefresh(force, false);
            BuildView();
        }
    }
}