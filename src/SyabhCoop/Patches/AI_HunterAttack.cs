// Патч AI_HunterAttack.PickClearanceSpot (лаги у роботов): игра искала
// ближайший узел A* (AstarPath.GetNearest, на сетке — расширяющийся поиск) и
// выбрасывала результат (AI_HunterAttack.cs:369-375, C_BotNavigator.cs:253-264).
// Поведение то же: точка «отойти вбок» = позиция + направление × шаг.
using UnityEngine;

class patch_AI_HunterAttack : AI_HunterAttack
{
    public new void PickClearanceSpot(Vector3 direction)
    {
        direction.y = 0f;
        m_ClearanceDest = transform.position;
        m_ClearanceDest += direction * m_ClearanceStepSize;
    }
}
