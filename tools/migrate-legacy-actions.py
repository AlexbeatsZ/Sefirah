"""Translate fork action settings into the official 3.1 ActionItem format.

Run only while Sefirah is stopped. The original settings are copied to the
explicit backup directory before atomically replacing the settings file.
"""
import json
import pathlib
import shutil
import sys

POWER_COMMANDS = {
    ('rundll32.exe', 'user32.dll,LockWorkStation'): ('Lock', 'Lock'),
    ('shutdown', '/h'): ('Hibernate', 'Recent'),
    ('shutdown', '/l'): ('LogOff', 'SignOut'),
    ('shutdown', '/r /t 0'): ('Restart', 'UpdateRestore'),
    ('shutdown', '/s /t 0'): ('Shutdown', 'PowerButton'),
    ('loginctl', 'lock-session'): ('Lock', 'Lock'),
    ('systemctl', 'hibernate'): ('Hibernate', 'Recent'),
    ('loginctl', 'terminate-session'): ('LogOff', 'SignOut'),
    ('shutdown', '-r now'): ('Restart', 'UpdateRestore'),
    ('shutdown', '-h now'): ('Shutdown', 'PowerButton'),
}
RUN_FIELDS = ('Path', 'Arguments', 'StartInDirectory', 'EnvironmentVariables', 'UseShellExecute', 'CreateNoWindow')


def convert(action):
    if 'ActionId' in action:
        return action, False
    kind = action.get('$type')
    item = {key: action[key] for key in ('Id', 'Name', 'Icon', 'AskForConfirmation') if key in action}
    command = (str(action.get('Path', '')).lower(), action.get('Arguments', ''))
    if kind == 'Process' and command in POWER_COMMANDS:
        power, icon = POWER_COMMANDS[command]
        item.update(ActionId='Power', Settings={'Kind': power}, Icon=icon)
        item.setdefault('AskForConfirmation', True)
    elif kind == 'Process':
        item.update(ActionId='Run', Settings={key: action[key] for key in RUN_FIELDS if key in action})
        item.setdefault('Icon', 'Apps')
    elif kind in ('Uri', 'URI', 'Link') and ('Uri' in action or 'Url' in action):
        item.update(ActionId='Link', Settings={'Url': action.get('Url', action.get('Uri'))})
        item.setdefault('Icon', 'Link')
    else:
        raise ValueError(f"Unsupported legacy action type for {action.get('Id')}: {kind}")
    return item, True


def main():
    path, backup_dir = map(pathlib.Path, sys.argv[1:3])
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    converted = [convert(action) for action in data.get('Actions', [])]
    changed = sum(changed for _, changed in converted)
    if changed:
        backup_dir.mkdir(parents=True, exist_ok=True)
        backup = backup_dir / 'user_settings-before-action-migration.json'
        if backup.exists():
            raise FileExistsError(backup)
        shutil.copy2(path, backup)
        data['Actions'] = [action for action, _ in converted]
        temp = path.with_name(path.name + '.migration.tmp')
        temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        temp.replace(path)
    print(json.dumps({'settings': str(path), 'migrated_actions': changed}))


if __name__ == '__main__':
    main()
