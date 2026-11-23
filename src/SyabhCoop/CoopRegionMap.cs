// Быстрая карта «пиксель → ближайшая ячейка Вороного» (C_WorldMaps.RegenerateRegionArray).
//
// Оригинал перебирает для каждого из m_Dim² пикселей ВСЕ ячейки и считает
// корень: под CrossOver это 79 с без кадров (лог: "RegenerateRegionArray took
// 79123ms"), и за это время клиенты теряли соединение с сервером (прогон s1b,
// LostConnection у обоих клиентов). Здесь тот же результат бит в бит, но
// кандидаты берутся из сетки корзин, кольцами вокруг пикселя.
//
// Точность: значение пикселя в оригинале — первый индекс l с минимальным
// t = (int)Mathf.Sqrt((float)(dx²+dy²)), где dx = |(int)cx - j|, dy = |(int)cy - k|,
// и только если t < 10000 (иначе остаётся значение прошлого пикселя, в начале -1).
// Мы считаем t тем же выражением и берём минимум пары (t, l) — это и есть
// «первый с минимальным t». Кольца заканчиваем, когда нижняя граница
// расстояния до следующего кольца LB > t_best: у всех дальних точек
// t >= LB > t_best (sqrt монотонен, sqrt(LB²) точен), они не выиграют даже
// при равенстве. Точки вне [0, m_Dim) кладём в крайние корзины: проекция на
// квадрат не увеличивает расстояние до пикселей внутри, граница остаётся верной.
// Совпадение с оригиналом проверено tools/test_region_map (случайные наборы)
// и в игре ключом -coopcheckregion (сравнение с оригиналом на реальном острове).
using System;

public static class CoopRegionMap
{
    public const int Bucket = 32;

    // как в оригинале: Mathf.Sqrt(float) = (float)Math.Sqrt((double)f)
    private static int T(int d2)
    {
        float f = d2;
        return (int)(float)Math.Sqrt((double)f);
    }

    // out[k + j*dim] = индекс ячейки (формат C_Array.Write(j, k, v))
    public static float[] Compute(int dim, int[] cx, int[] cy)
    {
        int count = cx.Length;
        var result = new float[dim * dim];
        int nb = (dim + Bucket - 1) / Bucket;
        if (nb < 1) nb = 1;
        // корзины: списки индексов по возрастанию (порядок не важен — сравниваем пару (t, l))
        var heads = new int[nb * nb + 1];
        var bx = new int[count]; var by = new int[count];
        for (int l = 0; l < count; l++)
        {
            bx[l] = Clamp(cx[l] / Bucket, cx[l], nb);
            by[l] = Clamp(cy[l] / Bucket, cy[l], nb);
            heads[bx[l] * nb + by[l] + 1]++;
        }
        for (int i = 1; i < heads.Length; i++) heads[i] += heads[i - 1];
        var items = new int[count];
        var fill = new int[nb * nb];
        for (int l = 0; l < count; l++)
        {
            int b = bx[l] * nb + by[l];
            items[heads[b] + fill[b]++] = l;
        }

        int num = -1;   // как в оригинале: живёт между пикселями
        for (int j = 0; j < dim; j++)
        {
            int pbx = Math.Min(j / Bucket, nb - 1);
            for (int k = 0; k < dim; k++)
            {
                int pby = Math.Min(k / Bucket, nb - 1);
                int bestT = int.MaxValue, bestL = int.MaxValue;
                for (int r = 0; r < nb; r++)
                {
                    int lb = r == 0 ? 0 : (r - 1) * Bucket + 1;
                    if (lb > bestT) break;
                    int x0 = pbx - r, x1 = pbx + r, y0 = pby - r, y1 = pby + r;
                    for (int gx = Math.Max(x0, 0); gx <= Math.Min(x1, nb - 1); gx++)
                    {
                        bool edgeX = gx == x0 || gx == x1;
                        for (int gy = Math.Max(y0, 0); gy <= Math.Min(y1, nb - 1); gy++)
                        {
                            if (!edgeX && gy != y0 && gy != y1) { gy = y1 - 1; continue; }  // только кольцо
                            int b = gx * nb + gy;
                            for (int s = heads[b], e = heads[b + 1]; s < e; s++)
                            {
                                int l = items[s];
                                int dx = cx[l] - j; if (dx < 0) dx = -dx;
                                int dy = cy[l] - k; if (dy < 0) dy = -dy;
                                int t = T(dx * dx + dy * dy);
                                if (t < bestT || (t == bestT && l < bestL)) { bestT = t; bestL = l; }
                            }
                        }
                    }
                }
                if (bestT < 10000) num = bestL;
                result[k + j * dim] = num;
            }
        }
        return result;
    }

    private static int Clamp(int b, int coord, int nb)
    {
        if (coord < 0) return 0;      // целочисленное деление к нулю: -5/32 = 0, но и так 0
        return b >= nb ? nb - 1 : b;
    }

    // оригинальный алгоритм (для проверки)
    public static float[] Reference(int dim, int[] cx, int[] cy)
    {
        int count = cx.Length;
        var result = new float[dim * dim];
        int num = -1;
        for (int j = 0; j < dim; j++)
            for (int k = 0; k < dim; k++)
            {
                int num3 = 10000;
                for (int l = 0; l < count; l++)
                {
                    int num4 = cx[l] - j; if (num4 < 0) num4 *= -1;
                    int num5 = cy[l] - k; if (num5 < 0) num5 *= -1;
                    float f = num4 * num4 + num5 * num5;
                    int num2 = (int)(float)Math.Sqrt((double)f);
                    if (num2 < num3) { num3 = num2; num = l; }
                }
                result[k + j * dim] = num;
            }
        return result;
    }
}
