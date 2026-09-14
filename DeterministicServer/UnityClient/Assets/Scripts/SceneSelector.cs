using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneSelector : MonoBehaviour
{
    // 버튼의 OnClick 이벤트에 연결할 메서드 1
    public void LoadECSScene()
    {
        // 씬 이름이 정확히 일치해야 합니다.
        SceneManager.LoadScene("ECSScene"); 
    }

    // 버튼의 OnClick 이벤트에 연결할 메서드 2
    public void LoadMonoScene()
    {
        SceneManager.LoadScene("MonoScene");
    }
}