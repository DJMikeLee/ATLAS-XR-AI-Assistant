using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples
{
    public class CapturedPicturesCarousel : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private PassthroughCameraPictureTaker pictureTaker;

        [Header("UI")]
        [SerializeField] private Transform contentParent;          // The "Content" GameObject
        [SerializeField] private GameObject carouselPicturePrefab; // Prefab with a RawImage

        private void OnEnable()
        {
            RebuildCarousel();
        }

        public void RebuildCarousel()
        {
            if (contentParent == null || carouselPicturePrefab == null || pictureTaker == null)
            {
                Debug.LogWarning("CapturedPicturesCarousel not fully configured.");
                return;
            }

            // Clear existing children
            for (int i = contentParent.childCount - 1; i >= 0; i--)
            {
                Destroy(contentParent.GetChild(i).gameObject);
            }

            IReadOnlyList<Texture2D> pics = pictureTaker.CapturedPictures;
            if (pics == null || pics.Count == 0) return;

            // Instantiate one entry per picture
            for (int i = 0; i < pics.Count; i++)
            {
                var go = Instantiate(carouselPicturePrefab, contentParent, false);

                var raw = go.GetComponent<RawImage>();
                if (raw != null)
                {
                    raw.texture = pics[i];
                }
                else
                {
                    Debug.LogWarning("Carousel Picture prefab has no RawImage.");
                }
            }
        }
    }
}
