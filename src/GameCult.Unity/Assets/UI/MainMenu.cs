using System;
using GameCult.Unity.UI.Components;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GameCult.Unity.UI
{
    public class MainMenu : MonoBehaviour
    {
        [SerializeField] private Prototype? panelPrototype;
        [SerializeField] private float fadeTime = .5f;
        [SerializeField] private float fadeDistance = 512;
        [SerializeField] private float fadeAlphaExponent = 2;
        [SerializeField] private float fadePositionExponent = 2;

        private (GeneratorPanel panel, CanvasGroup group) _currentMenu, _nextMenu;
        private bool _fadeFromRight;
        private float _fadeLerp;
        private bool _fading;
        private Vector3 _panelPosition;
        //private Task<DatabaseCache> _databaseLoad;
    
        void Start()
        {
            // Start loading the database in the background 'cause it takes a few seconds
            //_databaseLoad = Task.Run(() => ActionGameManager.Database);
            
            if(panelPrototype == null)
            {
                gameObject.SetActive(false);
                return;
            }
        
            _panelPosition = panelPrototype.transform.position;
        
            var panel1 = panelPrototype.Instantiate<GeneratorPanel>();
            _currentMenu = (panel1, panel1.GetComponent<CanvasGroup>());
        
            var panel2 = panelPrototype.Instantiate<GeneratorPanel>();
            _nextMenu = (panel2, panel2.GetComponent<CanvasGroup>());

            _currentMenu.panel.gameObject.SetActive(false);
            //_saveDirectory = ActionGameManager.GameDataDirectory.CreateSubdirectory("Saves");
        
            ShowMain();
            Fade(true);
        }

        private void Update()
        {
            if (_fading)
            {
                _fadeLerp += Time.deltaTime / fadeTime;

                _currentMenu.panel.transform.position = 
                    _panelPosition + (_fadeFromRight ? Vector3.left : Vector3.right) * (fadeDistance * pow(_fadeLerp, fadePositionExponent));
                _nextMenu.panel.transform.position = 
                    _panelPosition + (_fadeFromRight ? Vector3.right : Vector3.left) * (fadeDistance * pow(1-_fadeLerp, fadePositionExponent));
                _currentMenu.group.alpha = pow(1 - _fadeLerp, fadeAlphaExponent);
                _nextMenu.group.alpha = pow(_fadeLerp, fadeAlphaExponent);
            
                if (_fadeLerp > 1)
                {
                    _fading = false;
                    _currentMenu.panel.gameObject.SetActive(false);
                    (_currentMenu, _nextMenu) = (_nextMenu, _currentMenu);
                }
            }
        }
        
        private void Fade(bool fromRight)
        {
            _nextMenu.panel.gameObject.SetActive(true);
            _nextMenu.group.alpha = 0;
            _fading = true;
            _fadeLerp = 0;
            _fadeFromRight = fromRight;
        }

        private void ShowMain()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("CultUI", "Runtime UI Composition");
            _nextMenu.panel.AddTextButton("Demos",
                () =>
                {
                    ShowDemos();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Settings",
                () =>
                {
                    ShowSettings();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Quit", Application.Quit);
        }

        private void ShowDemos()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("CultUI", "Demos");
            _nextMenu.panel.AddTextButton("Procedural Inspector",
                () =>
                {
                    ShowProceduralInspectorDemo();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowMain();
                    Fade(false);
                });
        }

        private void ShowProceduralInspectorDemo()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("CultUI", "Code Inspector");
            _nextMenu.panel.AddLabel("For the common task of inspector-style menus with a label and a field, " +
                                     "CultUI offers convenient extensions " +
                                     "which compose a label and a field handler within a horizontal scope. " +
                                     "This can be great for options menus or in-game properties panels.");
            _nextMenu.panel.AddLabelInspector("Read-only String", () => $"{Time.time:F1}");
            var testString = "Test String";
            _nextMenu.panel.AddStringInspector("String", () => testString, s => testString = s);
            var testFloat = 10f;
            _nextMenu.panel.AddFloatInspector("Float", () => testFloat, f => testFloat = f);
            var testRangedFloat = 10f;
            _nextMenu.panel.AddRangedFloatInspector("Ranged Float", () => testRangedFloat, f => testRangedFloat = f, 0, 100);
            var testBool = true;
            _nextMenu.panel.AddBoolInspector("Bool", () => testBool, b => testBool = b);
            var testIncrement = 3;
            _nextMenu.panel.AddIncrementIntInspector("Increment Int", () => testIncrement, i => testIncrement = i, 0, 5);
            _nextMenu.panel.AddButtonInspector("Button", "Click me!", () => Debug.Log("Button Clicked!"));
            var testEnum = Animals.Dog;
            _nextMenu.panel.AddEnumInspector("Enum", () => testEnum, e => testEnum = e);
            var testFlags = AnimalTraits.HasFur;
            _nextMenu.panel.AddFlagsEnumInspector("Flags", () => testFlags, f => testFlags = f);
            _nextMenu.panel.AddProgressInspector("Progress", () => Mathf.PingPong(Time.time * 0.2f, 1f));
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowDemos();
                    Fade(false);
                });
        }
        
        [Flags]
        enum AnimalTraits
        {
            None = 0,
            HasFur = 1,
            CanFly = 2,
            CanSwim = 4,
            IsVenomous = 8,
            Nocturnal = 16
        }

        private enum Animals
        {
            Dog,
            Cat,
            Bird
        }

        private void ShowSettings()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.Title = "Settings";
            _nextMenu.panel.AddTextButton("Gameplay",
                () =>
                {
                    ShowGameplaySettings();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Graphics",
                () =>
                {
                    ShowGraphicsSettings();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Input",
                () =>
                {
                    ShowInputSettings();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Audio",
                () =>
                {
                    ShowAudioSettings();
                    Fade(true);
                });
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowMain();
                    Fade(false);
                });
        }
    
        private void ShowGameplaySettings()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("Gameplay", "Settings");
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowSettings();
                    Fade(false);
                });
        }

        private void ShowGraphicsSettings()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("Graphics", "Settings");
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowSettings();
                    Fade(false);
                });
        }

        private void ShowInputSettings()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("Input", "Settings");
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowSettings();
                    Fade(false);
                });
        }

        private void ShowAudioSettings()
        {
            _nextMenu.panel.Clear();
            _nextMenu.panel.TitleSubtitle("Audio", "Settings");
            _nextMenu.panel.AddTextButton("Back",
                () =>
                {
                    ShowSettings();
                    Fade(false);
                });
        }
    }
}
