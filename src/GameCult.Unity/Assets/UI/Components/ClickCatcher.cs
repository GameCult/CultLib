/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using R3;
using UnityEngine.EventSystems;

namespace GameCult.Unity.UI.Components
{
	public class ClickCatcher : ResolverComponent, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerClickHandler
	{
		public float dragDistance = 25f;
		
		public bool PointerIsInside { get; private set; }
	
		public Subject<PointerEventData> OnEnter = new Subject<PointerEventData>();
		public Subject<PointerEventData> OnExit = new Subject<PointerEventData>();
		public Subject<PointerEventData> OnClick = new Subject<PointerEventData>();
		public Subject<PointerEventData> OnDown = new Subject<PointerEventData>();
	
		public void OnPointerEnter(PointerEventData eventData)
		{
			//Debug.Log("Pointer Entered");
			OnEnter.OnNext(eventData);
			PointerIsInside = true;
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			//Debug.Log("Pointer Exited");
			OnExit.OnNext(eventData);
			PointerIsInside = false;
		}

		public void OnPointerDown(PointerEventData eventData)
		{
			OnDown.OnNext(eventData);
		}

		public void OnPointerClick(PointerEventData eventData)
		{
			if((eventData.pressPosition - eventData.position).sqrMagnitude < dragDistance)
				OnClick.OnNext(eventData);
		}

		private void OnDestroy()
		{
			OnEnter.Dispose();
			OnExit.Dispose();
			OnClick.Dispose();
			OnDown.Dispose();
		}
	}
}