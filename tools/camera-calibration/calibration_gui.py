"""Desktop workflow for the existing ChArUco calibration CLI."""
from datetime import datetime
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
import uuid
import webbrowser

ROOT = Path(__file__).resolve().parents[2]


def import_view(session, paths):
    import cv2 as cv
    import numpy as np
    root = Path(session)
    if not (root / 'session.json').is_file():
        raise ValueError('Сначала создайте или выберите сессию.')
    images = []
    for path in paths:
        image = cv.imdecode(np.frombuffer(Path(path).read_bytes(), np.uint8), cv.IMREAD_UNCHANGED)
        if image is None or image.dtype != np.uint8:
            raise ValueError('Для импорта выберите 8-битные PNG/JPEG/PGM/TIFF.')
        images.append(image)
    if len(images) != 3 or len({im.shape[:2] for im in images}) != 1:
        raise ValueError('Нужны три кадра одинакового разрешения.')
    view = root / 'views' / ('import-' + uuid.uuid4().hex[:12])
    view.mkdir(parents=True)
    for camera, path in zip('ABC', paths):
        shutil.copyfile(path, view / (camera + Path(path).suffix.lower()))
    return view


class CalibrationApp:
    def __init__(self, window):
        self.window, self.running = window, False
        self.events = queue.Queue()
        window.title('SmartMetrix — калибровка камер')
        window.geometry('1050x780')
        window.minsize(950, 700)
        window.protocol('WM_DELETE_WINDOW', self.close)
        body = ttk.Frame(window, padding=18)
        body.pack(fill='both', expand=True)
        ttk.Label(body, text='Калибровка трёх камер', font=('Segoe UI', 20, 'bold')).pack(anchor='w')
        ttk.Label(body, text='1. Сессия и шаблон → 2. Снимки → 3. Расчёт → 4. Экспорт').pack(anchor='w', pady=(0, 12))
        self.values = {k: tk.StringVar(value=v) for k, v in dict(
            session=str(ROOT / 'artifacts/calibration/hikvision'), rig='hikvision-1m', columns='10', rows='7', square='80',
            service='http://127.0.0.1:5102', output=str(ROOT / 'artifacts/calibration'), result='', pose='', a='', b='', c='').items()}
        self.controls = []
        self.stationary = tk.BooleanVar(value=False)
        self.lab = tk.BooleanVar(value=False)
        self.tabs = ttk.Notebook(body)
        self.tabs.pack(fill='both', expand=True)
        pages = []
        for label in ['1 · Сессия', '2 · Снимки', '3 · Расчёт', '4 · Экспорт']:
            page = ttk.Frame(self.tabs, padding=16)
            page.columnconfigure(1, weight=1)
            self.tabs.add(page, text=label)
            pages.append(page)
        self.field(pages[0], 0, 'Сессия', 'session', 'directory')
        self.field(pages[0], 1, 'Идентификатор рамы', 'rig')
        self.field(pages[0], 2, 'Столбцов клеток', 'columns')
        self.field(pages[0], 3, 'Строк клеток', 'rows')
        self.field(pages[0], 4, 'Сторона клетки, мм', 'square')
        self.button(pages[0], 5, 'Создать сессию и шаблон', self.init)
        self.button(pages[0], 6, 'Открыть шаблон для печати', lambda: self.open_file(Path(self.get('session')) / 'board-print.svg'))
        self.button(pages[0], 7, 'Открыть настройки сессии', lambda: self.open_file(Path(self.get('session')) / 'session.json'))
        self.note(pages[0], 8, 'Для существующей сессии выберите её каталог: повторное создание не нужно.\nПечать 100%, жёсткая плоская основа. Проверьте размеры линейкой.\nПо умолчанию ожидаемые базисы 0,25 / 0,75 / 1 м. Другую геометрию задайте\nв session.json до съёмки. Поля выше используются только при создании.')
        self.field(pages[1], 0, 'Адрес CameraService', 'service')
        check = ttk.Checkbutton(pages[1], text='Камеры и шаблон неподвижны до конца захвата', variable=self.stationary)
        check.grid(row=1, column=0, columnspan=3, sticky='w', pady=8)
        self.controls.append(check)
        self.button(pages[1], 2, 'Захватить положение A/B/C через RTSP', self.capture)
        for row, camera in enumerate('abc', 3):
            self.field(pages[1], row, 'Файл камеры ' + camera.upper(), camera, 'image')
        self.button(pages[1], 6, 'Добавить тройку файлов в сессию', self.import_images)
        self.button(pages[1], 7, 'Обновить число положений', self.refresh)
        self.count = tk.StringVar(value='Положения: —')
        ttk.Label(pages[1], textvariable=self.count).grid(row=8, column=0, columnspan=3, sticky='w')
        self.note(pages[1], 9, 'Снимите 15–25 разных положений: центр, края, наклоны и расстояния.\nТри файла относятся к одному неподвижному положению шаблона.\nДоступы RTSP задаются в CameraService. Arena: импорт сохранённых кадров.')
        self.field(pages[2], 0, 'Каталог для результатов', 'output', 'directory')
        self.button(pages[2], 1, 'Рассчитать калибровку', self.calculate)
        self.field(pages[2], 2, 'Полученный результат', 'result', 'directory')
        self.button(pages[2], 3, 'Открыть отчёт калибровки', lambda: self.open_file(Path(self.get('result')) / 'report.html'))
        self.note(pages[2], 4, 'Каждый расчёт создаёт новый каталог. Проверяются ошибки, охват кадра,\nбазисы и согласованность трёх пар. FAIL означает, что набор нужно исправить.\nДаже PASS нужно подтвердить измерениями известных размеров на других кадрах.')
        self.field(pages[3], 0, 'Риг → платформа (JSON)', 'pose', 'json')
        lab = ttk.Checkbutton(pages[3], text='Лабораторный стенд: платформа совпадает с камерой A', variable=self.lab)
        lab.grid(row=1, column=0, columnspan=3, sticky='w', pady=10)
        self.controls.append(lab)
        self.button(pages[3], 2, 'Экспортировать calibration-draft.json', self.export)
        self.button(pages[3], 3, 'Открыть сравнение глубины', self.comparison)
        self.note(pages[3], 4, 'Экспорт использует каталог результата на вкладке «Расчёт».\nДля реальной платформы укажите измеренное преобразование rotation/translation.\nЛабораторный вариант задаёт единичный поворот и нулевое смещение.\nРегистрация и активация калибровки в сервисе автоматически не выполняются.')
        self.status = tk.StringVar(value='Выберите существующую сессию или создайте новую.')
        ttk.Label(body, textvariable=self.status, wraplength=990).pack(anchor='w', pady=8)
        self.progress = ttk.Progressbar(body, mode='indeterminate')
        self.progress.pack(fill='x')
        self.log = tk.Text(body, height=9, state='disabled', wrap='word')
        self.log.pack(fill='x', pady=(8, 0))
        self.refresh()
        window.after(100, self.poll)

    def get(self, key):
        return self.values[key].get().strip()

    def field(self, page, row, label, key, kind=None):
        ttk.Label(page, text=label).grid(row=row, column=0, sticky='w', padx=(0, 10), pady=6)
        entry = ttk.Entry(page, textvariable=self.values[key])
        entry.grid(row=row, column=1, sticky='ew', pady=6)
        self.controls.append(entry)
        if kind:
            button = ttk.Button(page, text='Выбрать…', command=lambda: self.browse(key, kind))
            button.grid(row=row, column=2, padx=8)
            self.controls.append(button)

    def button(self, page, row, label, callback):
        button = ttk.Button(page, text=label, command=lambda: self.safe(callback))
        button.grid(row=row, column=0, columnspan=3, sticky='w', pady=7)
        self.controls.append(button)

    @staticmethod
    def note(page, row, text):
        ttk.Label(page, text=text, wraplength=900, foreground='#465567').grid(row=row, column=0, columnspan=3, sticky='w', pady=12)

    def safe(self, callback):
        try:
            callback()
        except (ValueError, OSError) as error:
            messagebox.showerror('Калибровка', str(error), parent=self.window)

    def browse(self, key, kind):
        path = filedialog.askdirectory(parent=self.window) if kind == 'directory' else filedialog.askopenfilename(parent=self.window, filetypes=[('JSON', '*.json')] if kind == 'json' else [('Изображения', '*.png *.jpg *.jpeg *.pgm *.tif *.tiff')])
        if path:
            self.values[key].set(path)
            self.refresh()

    def open_file(self, path):
        if not path.is_file():
            raise ValueError('Файл пока не создан: ' + str(path))
        webbrowser.open(path.resolve().as_uri())

    def session(self):
        root = Path(self.get('session'))
        if not (root / 'session.json').is_file():
            raise ValueError('Выберите существующую сессию на первой вкладке.')
        return root

    def refresh(self):
        views = Path(self.get('session')) / 'views'
        count = sum(1 for p in views.iterdir() if p.is_dir()) if views.is_dir() else 0
        self.count.set(f'Папок положений: {count}. При расчёте будут проверены общие точки и дубли.')

    def init(self):
        if not self.get('session') or not self.get('rig'):
            raise ValueError('Укажите путь сессии и идентификатор рамы.')
        self.run(['init', self.get('session'), '--rig-id', self.get('rig'), '--columns', self.get('columns'), '--rows', self.get('rows'), '--square-mm', self.get('square')], 'init')

    def capture(self):
        self.session()
        if not self.stationary.get():
            raise ValueError('Подтвердите неподвижность камер и шаблона перед захватом.')
        self.run(['capture', self.get('session'), '--camera-service', self.get('service'), '--stationary-confirmed'], 'capture')

    def import_images(self):
        self.session()
        if not self.stationary.get():
            raise ValueError('Подтвердите, что импортируемые кадры сняты при неподвижном шаблоне.')
        paths = [self.get(c) for c in 'abc']
        if any(not Path(p).is_file() for p in paths):
            raise ValueError('Выберите все три изображения.')
        session = self.get('session')
        self.begin()
        def work():
            try:
                view = import_view(session, paths)
                self.events.put(('log', f'Добавлено: {view}\n'))
                self.events.put(('done', ('import', 0)))
            except Exception as error:
                self.events.put(('log', str(error) + '\n'))
                self.events.put(('done', ('import', 1)))
        threading.Thread(target=work, daemon=True).start()

    def calculate(self):
        self.session()
        if not self.get('output'):
            raise ValueError('Укажите каталог результатов.')
        result = Path(self.get('output')).resolve() / datetime.now().strftime('result-%Y%m%d-%H%M%S-%f')
        self.values['result'].set(str(result))
        self.run(['calibrate', self.get('session'), '--output', str(result)], 'calibrate')

    def export(self):
        result = Path(self.get('result'))
        if not (result / 'calibration.json').is_file():
            raise ValueError('Выберите рассчитанную калибровку на вкладке «Расчёт».')
        pose = Path(self.get('pose'))
        if self.lab.get():
            pose = result / ('lab-pose-' + uuid.uuid4().hex[:8] + '.json')
            pose.write_text(json.dumps(dict(rotation=[1, 0, 0, 0, 1, 0, 0, 0, 1], translation=[0, 0, 0])), encoding='utf-8')
        elif not pose.is_file():
            raise ValueError('Выберите JSON преобразования или лабораторный вариант.')
        self.run(['export-draft', str(result), '--rig-to-platform', str(pose)], 'export')

    def comparison(self):
        subprocess.Popen([str(Path(sys.executable).with_name('pythonw.exe')) if os.name == 'nt' else sys.executable,
                          str(Path(__file__).with_name('compare_gui.py'))], cwd=ROOT,
                         creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))

    def begin(self):
        self.running = True
        for control in self.controls:
            control.configure(state='disabled')
        self.progress.start()
        self.status.set('Выполняется… Не перемещайте шаблон до завершения захвата.')
        self.log.configure(state='normal')
        self.log.delete('1.0', 'end')
        self.log.configure(state='disabled')

    def run(self, args, action):
        self.begin()
        def work():
            code = 1
            try:
                env = os.environ.copy()
                env['PYTHONIOENCODING'] = 'utf-8'
                with subprocess.Popen([sys.executable, '-u', str(Path(__file__).with_name('calibrate.py')), *args],
                                      cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env,
                                      text=True, encoding='utf-8', errors='replace', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)) as process:
                    for line in process.stdout:
                        self.events.put(('log', line))
                    code = process.wait()
            except Exception as error:
                self.events.put(('log', str(error) + '\n'))
            self.events.put(('done', (action, code)))
        threading.Thread(target=work, daemon=True).start()

    def poll(self):
        try:
            while True:
                kind, data = self.events.get_nowait()
                if kind == 'log':
                    self.log.configure(state='normal')
                    self.log.insert('end', data)
                    self.log.see('end')
                    self.log.configure(state='disabled')
                else:
                    action, code = data
                    self.running = False
                    self.progress.stop()
                    for control in self.controls:
                        control.configure(state='normal')
                    self.refresh()
                    self.status.set('Готово.' if code == 0 else 'Качество не прошло проверки — откройте отчёт.' if action == 'calibrate' and code == 2 else 'Операция не выполнена. Подробности в журнале.')
        except queue.Empty:
            pass
        self.window.after(100, self.poll)

    def close(self):
        if self.running:
            messagebox.showinfo('Выполняется операция', 'Дождитесь завершения операции перед закрытием окна.')
        else:
            self.window.destroy()


if __name__ == '__main__':
    window = tk.Tk()
    CalibrationApp(window)
    window.mainloop()
