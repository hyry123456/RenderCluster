using UnityEngine;
using UnityEngine.EventSystems;

namespace UI
{

    public class ClickDragUI : UIUseBase
    {
        Vector3 speed;
        Vector2 prePos;  //上一次的位置
        public Vector2 verticalRange = Vector2.zero, horizontalRange = Vector2.zero;
        RectTransform rectTransform;
        [Min(1f)]
        public float speedDamping = 10f;        //速度衰减
        protected override void Awake()
        {
            base.Awake();
            control.init += ShowSelf;
            widgrt.drag += Drag;
            widgrt.pointerDown = PointDown;
            widgrt.pointerUp = PointUp;
            rectTransform = GetComponent<RectTransform>();

            CaculateRange();
        }

        private void Update()
        {
            Vector3 pos = rectTransform.localPosition + speed * Time.deltaTime;
            if (pos.x < horizontalRange.x) pos.x = horizontalRange.x;
            if (pos.x > horizontalRange.y) pos.x = horizontalRange.y;
            if (pos.y < verticalRange.x) pos.y = verticalRange.x;
            if (pos.y > verticalRange.y) pos.y = verticalRange.y;
            rectTransform.localPosition = pos;
            speed *= (1.0f - speedDamping * Time.deltaTime);
        }

        private void CaculateRange()
        {
            //Rect rect = rectTransform.rect;
            //Vector3 leftUp = transform.localPosition;
            //leftUp.x -= rect.width / 2;
            //leftUp.y += rect.height / 2;
            //leftUp = rectTransform.TransformPoint(leftUp);

            //Vector3 rightDown = transform.localPosition;
            //rightDown.x += rect.width / 2;
            //rightDown.y -= rect.height / 2;
            //rightDown = rectTransform.TransformPoint(rightDown);

            horizontalRange.x += transform.localPosition.x;
            horizontalRange.y += transform.localPosition.x;
            verticalRange.x += transform.localPosition.y;
            verticalRange.y += transform.localPosition.y;
            //verticalRange.y = leftUp.y + verticalRange.y;
            //horizontalRange.x = rightDown.x + horizontalRange.x;
            //horizontalRange.y = leftUp.y + horizontalRange.y;
        }

        public void PointDown(PointerEventData eventData)
        {
            prePos = eventData.position;
            speed = Vector3.zero;
        }

        public void PointUp(PointerEventData eventData)
        {
            speed = (eventData.position - prePos)/Time.deltaTime;
            prePos = eventData.position;
        }

        public void Drag(PointerEventData eventData)
        {
            Vector3 offset = eventData.position - prePos;
            rectTransform.localPosition = rectTransform.localPosition + offset;
            prePos = eventData.position;
        }

#if UNITY_EDITOR

        //private void OnDrawGizmos()
        //{
        //    if (rectTransform == null)
        //        rectTransform = GetComponent<RectTransform>();
        //    if (rectTransform == null) return;
        //    Gizmos.color = Color.yellow;
        //    Rect rect = rectTransform.rect;
        //    Vector3 rightUp = transform.localPosition;
        //    rightUp.x -= rect.width / 2 - horizontalRange.x; 
        //    rightUp.y += rect.height / 2 + verticalRange.y;
        //    rightUp = transform.parent.TransformPoint(rightUp);

        //    Vector3 rightDown = transform.localPosition;
        //    rightDown.x -= rect.width / 2 - horizontalRange.x;
        //    rightDown.y -= rect.height / 2 - verticalRange.x;
        //    rightDown = transform.parent.TransformPoint(rightDown);

        //    Vector3 leftUp = transform.localPosition;
        //    leftUp.x += rect.width / 2 + horizontalRange.y;
        //    leftUp.y += rect.height / 2 + verticalRange.y;
        //    leftUp = transform.parent.TransformPoint(leftUp);

        //    Vector3 leftDown = transform.localPosition;
        //    leftDown.x += rect.width / 2 + horizontalRange.y;
        //    leftDown.y -= rect.height / 2 - verticalRange.x;
        //    leftDown = transform.parent.TransformPoint(leftDown);
        //    Gizmos.DrawLine(rightUp, rightDown);
        //    Gizmos.DrawLine(rightUp, leftUp);
        //    Gizmos.DrawLine(leftUp, leftDown);
        //    Gizmos.DrawLine(rightDown, leftDown);
        //}

        private void OnValidate()
        {
            verticalRange.x = Mathf.Min(verticalRange.x, 0);
            verticalRange.y = Mathf.Max(verticalRange.y, 0);
            horizontalRange.x = Mathf.Min(horizontalRange.x, 0);
            horizontalRange.y = Mathf.Max(horizontalRange.y, 0);
        }
#endif

    }
}