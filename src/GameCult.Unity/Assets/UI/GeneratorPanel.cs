using TMPro;
using UnityEngine;

namespace GameCult.Unity.UI
{
    public class GeneratorPanel : Generator
    {
        [SerializeField] private TextMeshProUGUI? title;
        
        public string Title
        {
            set
            {
                if (title is not null) title.text = value;
            }
        }

        public void TitleSubtitle(string title, string subtitle) => Title = $"{title}\n<smallcaps><size=50%>{subtitle}";
    }
}