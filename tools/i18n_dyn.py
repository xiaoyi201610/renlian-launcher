# -*- coding: utf-8 -*-
"""把一批动态句（含 {} 占位）和零散字面键追加进 static/i18n.js。
写入 I18N-SRC-END 之前，四语种各一个 Object.assign 块。"""
import io, json

ENTRIES = [
    # 注册工坊订阅
    ('已注册 {} 个新订阅', 'Registered {} new subscriptions', '{} nouveaux abonnements enregistrés', '{} neue Abonnements registriert', '{} nya prenumerationer registrerade'),
    ('没有需要注册的新订阅', 'No new subscriptions to register', 'Aucun nouvel abonnement à enregistrer', 'Keine neuen Abonnements zu registrieren', 'Inga nya prenumerationer att registrera'),
    ('已注册 {} 个', 'Registered {}', '{} enregistrés', '{} registriert', '{} registrerade'),
    ('没有需要注册的', 'Nothing to register', 'Rien à enregistrer', 'Nichts zu registrieren', 'Inget att registrera'),
    # 收藏夹
    ('收藏夹「{}」', 'Collection « {} »', 'Collection « {} »', 'Sammlung „{}“', 'Samling »{}»'),
    ('已收藏到「{}」', 'Added to « {} »', 'Ajouté à « {} »', 'Zu „{}“ hinzugefügt', 'Tillagd i »{}»'),
    ('已从「{}」移除', 'Removed from « {} »', 'Retiré de « {} »', 'Aus „{}“ entfernt', 'Borttagen från »{}»'),
    # 导出 / 保存 / 导入
    ('已导出 {} 个文件（{}MB）到：{}', 'Exported {} files ({}MB) to: {}', '{} fichiers ({} Mo) exportés vers : {}', '{} Dateien ({}MB) exportiert nach: {}', 'Exporterade {} filer ({}MB) till: {}'),
    ('已保存 {} 个模组', 'Saved {} mods', '{} mods enregistrés', '{} Mods gespeichert', 'Sparade {} moddar'),
    ('已保存 {} 个模组 · 已同步到游戏', 'Saved {} mods · synced to game', '{} mods enregistrés · synchronisé au jeu', '{} Mods gespeichert · mit Spiel synchronisiert', 'Sparade {} moddar · synkade med spelet'),
    ('已同步到游戏', 'synced to game', 'synchronisé au jeu', 'mit Spiel synchronisiert', 'synkad med spelet'),
    ('导入成功：{} 个模组', 'Import succeeded: {} mods', 'Import réussi : {} mods', 'Import erfolgreich: {} Mods', 'Import lyckades: {} moddar'),
    ('导入成功：{} 个模组，{} 个缺失', 'Import succeeded: {} mods, {} missing', 'Import réussi : {} mods, {} manquants', 'Import erfolgreich: {} Mods, {} fehlend', 'Import lyckades: {} moddar, {} saknas'),
    # 确认弹窗
    ('确定删除播放集「{}」？ 删除前会自动备份 launcher-v2.sqlite，可从备份目录恢复。', 'Delete playset "{}"?  launcher-v2.sqlite is auto-backed-up before deletion; restore from the backups folder.', "Supprimer l'ensemble « {} » ?  launcher-v2.sqlite est sauvegardé automatiquement avant suppression ; restaurez depuis le dossier de sauvegardes.", 'Spielset "{}" löschen?  launcher-v2.sqlite wird vor dem Löschen automatisch gesichert; Wiederherstellung aus dem Backup-Ordner.', 'Ta bort spelset »{}»?  launcher-v2.sqlite backas upp automatiskt innan borttagning; återställ från backups-mappen.'),
    ('有 {} 个创意工坊新订阅还没生成本地描述文件（列表里标为「未识别」），点这里一键生成', '{} Workshop subscriptions still lack a local descriptor file (shown as "Unrecognized"); click to generate them all at once.', '{} abonnements de l’Atelier n’ont pas encore de fichier descriptif local (marqués « Non reconnu ») ; cliquez pour les générer tous.', '{} Workshop-Abonnements haben noch keine lokale Beschreibungsdatei (als „Nicht erkannt“ markiert); klicken Sie, um sie alle zu erzeugen.', '{} Workshop-prenumerationer saknar fortfarande en lokal beskrivningsfil (visas som ”Oigenkänd”); klicka för att skapa alla på en gång.'),
    ('确定把 {} 个存档移入回收站？ 删除后可以从 Windows 回收站还原。', 'Move {} saves to the Recycle Bin?  You can restore them from the Windows Recycle Bin afterwards.', 'Déplacer {} sauvegardes vers la corbeille ?  Vous pouvez les restaurer depuis la corbeille Windows.', '{} Spielstände in den Papierkorb verschieben?  Danach können Sie sie aus dem Windows-Papierkorb wiederherstellen.', 'Flytta {} sparningar till papperskorgen?  Du kan återställa dem från Windows papperskorg efteråt.'),
    ('确定清空全部 {} 份备份？ 清空后无法靠备份回滚，但不会影响当前游戏数据。', 'Clear all {} backups?  After clearing you cannot roll back via backups, but current game data is unaffected.', 'Effacer toutes les {} sauvegardes ?  Après suppression, impossible de revenir en arrière via les sauvegardes, mais les données de jeu actuelles ne sont pas affectées.', 'Alle {} Backups löschen?  Danach kein Rollback über Backups möglich, aber die aktuellen Spieledaten bleiben unberührt.', 'Rensa alla {} säkerhetskopior?  Efter rensning går det inte att rulla tillbaka via backups, men nuvarande speldata påverkas inte.'),
    ('确定用这个备份覆盖当前文件？ {} 当前文件会先自动备份一份，出问题还能再切回来。', 'Overwrite current files with this backup? {}  The current files are auto-backed-up first; you can switch back if something breaks.', "Écraser les fichiers actuels avec cette sauvegarde ? {}  Les fichiers actuels sont d’abord sauvegardés automatiquement ; vous pouvez revenir en arrière si besoin.", 'Aktuelle Dateien mit diesem Backup überschreiben? {}  Die aktuellen Dateien werden zuerst automatisch gesichert; bei Problemen können Sie zurückwechseln.', 'Skriv över nuvarande filer med denna säkerhetskopia? {}  Nuvarande filer backas upp automatiskt först; om något går sönder kan du byta tillbaka.'),
    # DLC / 补丁
    ('已从 Steam 更新 DLC 清单（{} 个）并重新校验拥有情况', 'Updated the DLC list from Steam ({} items) and re-verified ownership', 'Liste DLC mise à jour depuis Steam ({} éléments) et ré-verrouillée', 'DLC-Liste von Steam aktualisiert ({} Einträge) und Besitz neu verifiziert', 'Uppdaterade DLC-listan från Steam ({} objekt) och verifierade ägande på nytt'),
    ('已重新校验：{} 个已拥有', 'Re-verified: {} owned', 'Revérifié : {} possédés', 'Neu verifiziert: {} besessen', 'Omverifierat: {} ägda'),
    ('（补丁解锁）', '(unlocked by patch)', '(débloqué par patch)', '(per Patch freigeschaltet)', '(upplåst av patch)'),
    ('（其中 {} 个靠本地文件判定）', '(of which {} judged by local files)', '(dont {} jugés par fichiers locaux)', '(davon {} über lokale Dateien beurteilt)', '(varav {} bedömda via lokala filer)'),
    ('检测到第三方 DLC 解锁补丁', 'Third-party DLC unlock patch detected', 'Patch de déblocage DLC tiers détecté', 'DLC-Unlock-Patch eines Drittanbieters erkannt', 'Tredjeparts DLC-upplåsningspatch upptäckt'),
    ('解锁 {} 个 DLC', 'unlocks {} DLC', 'débloque {} DLC', 'schaltet {} DLC frei', 'låser upp {} DLC'),
    ('本页不锁定', 'not locked on this page', 'non verrouillé sur cette page', 'auf dieser Seite nicht gesperrt', 'låses inte på denna sida'),
    # 压缩包安装 / 收集
    ('正在安装 Mod（{} 个）', 'Installing mods ({} files)', 'Installation des mods ({} fichiers)', 'Mods werden installiert ({} Dateien)', 'Installerar mods ({} filer)'),
    ('已安装 {} 个 Mod', 'Installed {} mods', '{} mods installés', '{} Mods installiert', 'Installerade {} moddar'),
    ('，{} 个失败', ', {} failed', ', {} échoués', ', {} fehlgeschlagen', ', {} misslyckades'),
    ('✗ 超过 {}MB，请先手动解压后把文件夹放进 mod 目录', '✗ Over {}MB; please unzip first, then put the folder into the mod directory', '✗ Plus de {} Mo ; décompressez d’abord, puis placez le dossier dans le répertoire des mods', '✗ Über {}MB; bitte zuerst entpacken, dann den Ordner ins Mod-Verzeichnis legen', '✗ Över {}MB; packa upp först och lägg sedan mappen i mod-katalogen'),
    ('上传 {}%', 'Uploading {}%', 'Envoi {}%', 'Hochladen {}%', 'Laddar upp {}%'),
    ('解压中…', 'Extracting…', 'Extraction…', 'Entpacken…', 'Extraherar…'),
    ('收集完成 · {} MB · 用时 {} 秒', 'Collection done · {} MB · took {} s', 'Collecte terminée · {} Mo · {} s', 'Sammlung fertig · {} MB · {} s', 'Insamling klar · {} MB · {} s'),
    # 内存 / 存档 / 工坊统计 / 设置
    ('全球 {}%', 'Global {}%', 'Monde {}%', 'Global {}%', 'Globalt {}%'),
    ('设置已保存 · 识别到 {} 个模组', 'Settings saved · {} mods detected', 'Paramètres enregistrés · {} mods détectés', 'Einstellungen gespeichert · {} Mods erkannt', 'Inställningar sparade · {} moddar upptäckta'),
    ('已移入回收站 {} 个', 'Moved {} to Recycle Bin', '{} déplacés vers la corbeille', '{} in den Papierkorb verschoben', '{} flyttade till papperskorgen'),
    ('存档 {} 个 · 模组 {} 个', '{} saves · {} mods', '{} sauvegardes · {} mods', '{} Spielstände · {} Mods', '{} sparningar · {} moddar'),
    ('本地 {} 个 · 工坊 {} 个', '{} local · {} Workshop', '{} local · {} Atelier', '{} lokal · {} Workshop', '{} lokala · {} Workshop'),
    ('共 {} GB', 'Total {} GB', 'Total {} Go', 'Gesamt {} GB', 'Totalt {} GB'),
    ('已使用', 'Used', 'Utilisé', 'Verwendet', 'Använt'),
    ('已解锁', 'Unlocked', 'Débloqués', 'Freigeschaltet', 'Upplåsta'),
    ('完成度', 'Completion', 'Achèvement', 'Vollständigkeit', 'Slutförande'),
    ('点击停用', 'Click to disable', 'Cliquer pour désactiver', 'Zum Deaktivieren klicken', 'Klicka för att inaktivera'),
    ('点击启用', 'Click to enable', 'Cliquer pour activer', 'Zum Aktivieren klicken', 'Klicka för att aktivera'),
    ('本地模组（无创意工坊 ID）', 'Local mod (no Workshop ID)', 'Mod local (sans ID Atelier)', 'Lokaler Mod (ohne Workshop-ID)', 'Lokal mod (ingen Workshop-ID)'),
    ('创意工坊 ID：', 'Workshop ID: ', 'ID Atelier : ', 'Workshop-ID: ', 'Workshop-ID: '),
    ('补丁可能已失效', 'The patch may no longer work', 'Le patch peut ne plus fonctionner', 'Der Patch funktioniert möglicherweise nicht mehr', 'Patchen fungerar kanske inte längre'),
    ('无法连接本地服务', 'Cannot connect to local service', 'Impossible de se connecter au service local', 'Verbindung zum lokalen Dienst fehlgeschlagen', 'Kan inte ansluta till lokal tjänst'),
    ('读取失败：', 'Read failed: ', 'Échec de lecture : ', 'Lesen fehlgeschlagen: ', 'Läsning misslyckades: '),
    ('已回退到本地文件判断：本地 dlc 目录里有 .dlc 文件的记为「文件已存在」', 'Fell back to local-file judgement: a .dlc file present in the local DLC folder counts as "file exists"', 'Repli sur le jugement par fichier local : un fichier .dlc présent dans le dossier DLC local compte comme « fichier existant »', 'Zurückgefallen auf lokale Dateiprüfung: eine .dlc-Datei im lokalen DLC-Ordner zählt als „Datei vorhanden“', 'Föll tillbaka på lokal filbedömning: en .dlc-fil i den lokala DLC-mappen räknas som ”fil finns”'),
]

LANGS = ['en', 'fr', 'de', 'sv']

def main():
    p = 'static/i18n.js'
    s = io.open(p, encoding='utf-8').read()
    marker = '/* I18N-SRC-END */'
    assert marker in s, 'marker missing'
    block = '\n'
    for li, lang in enumerate(LANGS):
        block += '/* ---- 动态句补丁（追加） ---- */\n'
        block += 'Object.assign(I18N_SRC.%s, {\n' % lang
        for row in ENTRIES:
            zh = row[0]
            val = row[1 + li]
            block += '  %s: %s,\n' % (json.dumps(zh, ensure_ascii=False), json.dumps(val, ensure_ascii=False))
        block += '});\n'
    s = s.replace(marker, block + marker, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('inserted %d entries x %d langs = %d keys' % (len(ENTRIES), len(LANGS), len(ENTRIES) * len(LANGS)))

if __name__ == '__main__':
    main()
