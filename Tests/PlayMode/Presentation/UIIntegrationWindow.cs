using UnityEngine.UI;

namespace ArkFramework.Tests
{
    public sealed class UIIntegrationWindow : UIWindow
    {
        public Button ActionButton =>
            GetComponentInChildren<Button>(includeInactive: true);
    }
}
