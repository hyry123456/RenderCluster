using UnityEngine;

public class CameraMove : MonoBehaviour
{
    float speed;

    private void Update()
    {
        if (Input.GetKey(KeyCode.LeftShift))
        {
            speed = Mathf.Max(20, speed);
            speed = speed + Time.deltaTime * 10;
        }
        else
            speed = 10.0f;
        if (Input.GetKey(KeyCode.Mouse1))
        {
            if (Input.GetKey(KeyCode.W))
            {
                transform.position += transform.forward * Time.deltaTime * speed;
            }
            if (Input.GetKey(KeyCode.S))
            {
                transform.position -= transform.forward * Time.deltaTime * speed;
            }
            if (Input.GetKey(KeyCode.A))
            {
                transform.position -= transform.right * Time.deltaTime * speed;
            }
            if (Input.GetKey(KeyCode.D))
            {
                transform.position += transform.right * Time.deltaTime * speed;
            }
            if (Input.GetKey(KeyCode.E))
            {
                transform.position += transform.up * Time.deltaTime * speed;
            }
            if (Input.GetKey(KeyCode.Q))
            {
                transform.position -= transform.up * Time.deltaTime * speed;
            }
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");
            transform.rotation = transform.rotation * Quaternion.Euler(-mouseY, mouseX, 0);
            Vector3 elur = transform.eulerAngles;
            transform.rotation = Quaternion.Euler(elur.x, elur.y, 0);
        }

        if (Input.GetKey(KeyCode.LeftAlt)){
            if (Input.GetKey(KeyCode.Mouse0))
            {
                float mouseX = Input.GetAxis("Mouse X");
                float mouseY = Input.GetAxis("Mouse Y");
                Vector3 position = transform.up * mouseY * 0.1f + transform.forward * mouseX * 0.1f + transform.position;
                transform.position = position;
            }
        }
    }
}
