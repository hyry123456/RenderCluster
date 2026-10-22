using UnityEngine;
using Common;
namespace Interaction
{
    public delegate void RecallDelegate(INonReturnAndNonParam recall);

    public class InteracteDelegate : InteractionBase
    {
        private RecallDelegate interacteDelegate;
        public RecallDelegate Delegate
        {
            set { interacteDelegate = value; }
        }

        public override void InteractionBehavior(Common.INonReturnAndNonParam recall)
        {
            if(interacteDelegate != null)
            {
                interacteDelegate(recall);
            }
        }

        protected override void OnEnable()
        {

        }
    }
}