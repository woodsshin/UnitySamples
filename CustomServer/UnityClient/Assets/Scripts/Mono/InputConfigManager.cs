using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class InputConfigManager : MonoBehaviour
{
    public static InputConfigManager Instance { get; private set; }

    [Header("Input Action Asset (Optional)")]
    [SerializeField] private InputActionAsset _inputActions;

    private InputActionMap _playerMap;
    private InputAction _moveAction;
    private string _filePath;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        _filePath = Path.Combine(Application.persistentDataPath, "input_bindings.json");
        InitializeActions();
        LoadBindingOverrides();
    }

    private void InitializeActions()
    {
        if (_inputActions == null)
        {
            // 인스펙터에 InputActionAsset이 없을 경우 동적으로 생성
            _inputActions = ScriptableObject.CreateInstance<InputActionAsset>();
            _playerMap = _inputActions.AddActionMap("Player");

            // [수정] AddAction 호출 후 expectedControlType 속성 직접 설정
            _moveAction = _playerMap.AddAction("Move", InputActionType.Value);
            _moveAction.expectedControlType = "Vector2";

            // 기본 바인딩 1: WASD
            _moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");

            // 기본 바인딩 2: 방향키
            _moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
        }
        else
        {
            _playerMap = _inputActions.FindActionMap("Player");
            _moveAction = _playerMap.FindAction("Move");
        }

        _inputActions.Enable();
    }

    /// <summary>
    /// 클라이언트 이동 입력 Vector2 반환
    /// </summary>
    public Vector2 GetMovementInput()
    {
        if (_moveAction == null) return Vector2.zero;
        return _moveAction.ReadValue<Vector2>();
    }

    /// <summary>
    /// 변경된 키 바인딩 정보를 JSON 파일로 저장
    /// </summary>
    public void SaveBindingOverrides()
    {
        if (_inputActions == null) return;

        try
        {
            string json = _inputActions.SaveBindingOverridesAsJson();
            File.WriteAllText(_filePath, json);
            Debug.Log($"[InputConfigManager] Saved binding overrides to {_filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[InputConfigManager] Failed to save bindings: {ex.Message}");
        }
    }

    /// <summary>
    /// JSON 파일에서 저장된 키 바인딩 오버라이드 정보를 로드
    /// </summary>
    public void LoadBindingOverrides()
    {
        if (!File.Exists(_filePath) || _inputActions == null) return;

        try
        {
            string json = File.ReadAllText(_filePath);
            _inputActions.LoadBindingOverridesFromJson(json);
            Debug.Log($"[InputConfigManager] Loaded binding overrides from {_filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[InputConfigManager] Failed to load bindings: {ex.Message}");
        }
    }

    private void OnEnable()
    {
        _inputActions?.Enable();
    }

    private void OnDisable()
    {
        _inputActions?.Disable();
    }
}