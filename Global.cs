/*
 *
 * This file is part of the DocGOST project.    
 * Copyright (C) 2018 Vitalii Nechaev.
 * 
 * This program is free software; you can redistribute it and/or modify it 
 * under the terms of the GNU Affero General Public License version 3 as 
 * published by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the 
 * GNU Affero General Public License for more details.
 * 
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 * 
 */

using System;

namespace DocGOST
{
    public class Global
    {
        // Перечисление для разделов спецификации в соответствии с ГОСТ 2.106-96
        public enum SpSections
        {
            Documentation = 1, // Документация
            Compleksi, // Комплексы
            SborEd, // Сборочные единицы
            Details, // Детали
            Standard, // Стандартные изделия
            Other, // Прочие изделия
            Materials, // Материалы
            Compleсts // Комплекты

        }

      

        // Операции с ID для спецификации и перечня

        public const int TempStartPos = 20;
        public const int TempStartPosMask = 0xFFFFF;

        public int makeID(int strNum, int tempNum)
        {
            return (strNum & TempStartPosMask) + (tempNum << TempStartPos);
        }

        public int getStrNum(int id)
        {
            return id & TempStartPosMask;
        }

        public int getTempNum(int id)
        {
            return (id >> TempStartPos);
        }

        public static long GetDesignatorValue(string designator, Action<string> OnError = null) {
            long IntOnError() {
                OnError?.Invoke($"Не удаётся распознать позиционное обозначение '{designator}'");
                return 0;
            }

            // C15, 1C15, C1-15
            if (designator == "") return 0;
            long result = 0;
            int idx = 0;
            int des_len = designator.Length;
            int prefix = 0;
            for (; idx < des_len && Char.IsDigit(designator[idx]); idx++)
                prefix = prefix * 10 + (designator[idx] - '0');
            int des_start = idx;
            for (; idx < des_len && !Char.IsDigit(designator[idx]); idx++)
                if (!Char.IsLetter(designator[idx]))
                    return IntOnError();
            int dl = idx - des_start;
            if (dl == 0 || dl > 3)
                return IntOnError();
            for (int i = 0; i < dl; ++i) {
                result += (long)((byte)designator[des_start + i]) << (56 - (i * 8));
            }
            int suffix = 0;
            for (; idx < des_len && Char.IsDigit(designator[idx]); idx++)
                suffix = suffix * 10 + (designator[idx] - '0');
            if (suffix == 0)
                return IntOnError();
            if (idx < des_len) {
                if (prefix != 0 || designator[idx] != '-')
                    return IntOnError();
                idx++;
                prefix = suffix;
                suffix = 0;
                for (; idx < des_len && Char.IsDigit(designator[idx]); idx++)
                    suffix = suffix * 10 + (designator[idx] - '0');
                if (suffix == 0 || idx < des_len)
                    return IntOnError();
            }
            result += (prefix << 16) + suffix;
            return result;
        }
    }
}
