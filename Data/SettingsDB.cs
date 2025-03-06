﻿/*
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

using SQLite;

namespace DocGOST.Data
{
    class SettingsDB
    {
        SQLiteConnection db;

        public SettingsDB()
        {
            string databasePath = "settings.sGOST";
            db = new SQLiteConnection(databasePath);

            db.CreateTable<SettingsItem>();
        }

        public int SaveSettingItem(SettingsItem item)
        {
            return db.InsertOrReplace(item);
        }

        public int DeleteSettingsItem(SettingsItem item)
        {
            return db.Table<SettingsItem>().Delete(x => x.name == item.name);
        }

        public int GetLength()
        {
            return db.Table<SettingsItem>().OrderByDescending(p => p.name).Count();
        }

        public SettingsItem GetItem(string name)
        {
            return db.Table<SettingsItem>().Where(x => x.name == name).FirstOrDefault();
        }

        public void BeginTransaction() {
            db.BeginTransaction();
        }

        public void Commit() {
            db.Commit();
        }
    }
}
