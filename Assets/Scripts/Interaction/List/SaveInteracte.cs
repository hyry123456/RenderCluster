
namespace Interaction
{
    public class SaveInteracte : InteractionBase
    {
        public override void InteractionBehavior(Common.INonReturnAndNonParam recall)
        {
            Common.DataSave.Instance.SaveData();
            recall();
        }

        protected override void OnEnable()
        {
        }
    }
}