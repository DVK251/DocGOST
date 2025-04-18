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
using System.Collections.Generic;
using static DocGOST.Global;

namespace DocGOST
{
    static class SpSectionsExtensions
    {
        public static string FullName(this SpSections sec) {
            switch (sec) {
                case SpSections.Documentation: return "Документация";
                case SpSections.Compleksi: return "Комплексы";
                case SpSections.SborEd: return "Сборочные единицы";
                case SpSections.Details: return "Детали";
                case SpSections.Standard: return "Стандартные изделия";
                case SpSections.Other: return "Прочие изделия";
                case SpSections.Materials: return "Материалы";
                case SpSections.Compleсts: return "Комплекты";
                default: return "";
            }
        }
    }

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
            if (dl == 0 || dl > 4)
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

        public static string ExtractDesignatorGroupName(long designatorValue) {
            string rslt = "";
            for (int i = 0; i < 4; i++) { 
                byte b = (byte)(designatorValue >> (56 - i * 8));
                if (b == 0) break;
                rslt += (char)b;
            }
            return rslt;
        }

        public static int ExtractDesignatorHieBlockNum(long designatorValue) {
            return (short)(designatorValue >> 16);
        }

        public static int ExtractDesignatorSelfNum(long designatorValue) {
            return (short)(designatorValue);
        }

        public static long ExtractDesignatorGroupAndSelfNum(long designatorValue) {
            return designatorValue & unchecked((long)0xFFFFFFFF0000FFFFUL);
        }

        public static long SwapDesignatorGroupAndSelfNum(long designatorValue) {
            return (designatorValue & unchecked((long)0xFFFFFFFF00000000UL)) | ((designatorValue & 0xFFFF) << 16) | ((designatorValue & 0xFFFF0000) >> 16);
        }

        public static string ParseItersTillLen(ref string str, int maxLineLength, string delimiter = " ") {
            string rslt = "";
            if (str.Length <= maxLineLength) {
                rslt = str.Trim();
                str = "";
                return rslt;
            }
            bool bNeedInsDelimiter = false;
            while (str != "") {
                int pos = str.IndexOf(delimiter);
                if (pos == -1)
                    pos = str.Length;
                if (rslt.Length + pos + (bNeedInsDelimiter ? delimiter.Length : 0) > maxLineLength) {
                    if (rslt == "") { // нет ни одного пробела на всю строку - принудительно прерываем
                        rslt = str.Substring(0, maxLineLength);
                        str = str.Substring(maxLineLength);
                    }
                    break;
                }
                string s2 = str.Substring(0, pos);
                str = str.Substring(pos + delimiter.Length);
                if (s2 != "") {
                    if (bNeedInsDelimiter) rslt += delimiter;
                    rslt += s2;
                    bNeedInsDelimiter = true;
                }
            }
            // Удаляем завершающие пробелы
            //if (delimiter == " ") { 
            //    foreach (char c in str)
            //        if (c != ' ') return rslt;
            //}
            //str = "";
            return rslt;
        }

        public static string ParseIter(ref string str, char delimiter = ' ') {
            int pos = str.IndexOf(delimiter);
            if (pos == -1) 
                pos = str.Length;
            string rslt = str.Substring(0, pos);
            str = str.Substring(pos + 1);
            return rslt;
        }

        // Используется для рисования пользовательской таблицы вместо той части 1-го листа штампа документа, на которой отображаются поля 24 и 25 ("Справ. №" и "Перв. примен.")
        public class Custom_24_25: List<( float YPos, float Height, List<(float width_mm, string text, bool is_align_center)> cells )> {
        }
    }
}