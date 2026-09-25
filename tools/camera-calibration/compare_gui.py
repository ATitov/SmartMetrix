"""Desktop launcher for the production depth comparison utility."""
import json
import math
from pathlib import Path
import queue
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from datetime import datetime
import webbrowser

ROOT = Path(__file__).resolve().parents[2]


def command(values):
    calibration = Path(values['calibration']).expanduser().resolve()
    if not (calibration / 'calibration-draft.json').is_file():
        raise ValueError('Выберите каталог с calibration-draft.json и картами NPZ.')
    for name in 'abc':
        if not values[name].strip() or not Path(values[name]).is_file():
            raise ValueError(f'Выберите изображение камеры {name.upper()}.')
    near, far = float(values['near']), float(values['far'])
    disparity = int(values['disparity'])
    if not math.isfinite(near) or not math.isfinite(far) or not 0 < near < far or disparity < 1:
        raise ValueError('Нужны 0 < ближняя граница < дальняя граница и disparity ≥ 1.')
    if not values['output'].strip():
        raise ValueError('Выберите каталог для результатов.')
    output = Path(values['output']).resolve() / datetime.now().strftime('run-%Y%m%d-%H%M%S-%f')
    args = [sys.executable, '-u', str(Path(__file__).with_name('compare_depth.py')), str(calibration),
            '--output', str(output), '--near', str(near), '--far', str(far), '--max-disparity', str(disparity)]
    for name in 'abc':
        args.extend([f'--{name}', str(Path(values[name]).resolve())])
    if values['roi'].strip():
        roi = values['roi'].split()
        if len(roi) != 4 or any(not v.isdigit() for v in roi) or min(map(int, roi[2:])) <= 0:
            raise ValueError('Область: четыре целых числа X Y ширина высота; размеры больше нуля.')
        args.extend(['--roi', *roi])
    if values['reference'].strip():
        reference = float(values['reference'])
        if not values['roi'].strip() or not math.isfinite(reference) or reference <= 0:
            raise ValueError('Для эталонной глубины задайте область мишени и положительное число метров.')
        args.extend(['--reference-depth', str(reference)])
    return args, output


class App:
    def __init__(self, window):
        self.window = window
        self.running = False
        self.report = None
        self.events = queue.Queue()
        self.preview_image = None
        window.title('SmartMetrix — сравнение глубины')
        window.geometry('1100x850')
        window.minsize(900, 700)
        window.protocol('WM_DELETE_WINDOW', self.close)
        panel = ttk.Frame(window, padding=18)
        panel.pack(fill='both', expand=True)
        panel.columnconfigure(1, weight=1)
        ttk.Label(panel, text='Сравнение стереопар', font=('Segoe UI', 20, 'bold')).grid(row=0, column=0, columnspan=3, sticky='w')
        ttk.Label(panel, text='AB • AC • BC • AB+AC • AB+AC+BC   |   Исходные кадры неподвижной сцены').grid(row=1, column=0, columnspan=3, sticky='w', pady=(0, 14))
        defaults = dict(calibration='', a='', b='', c='', output=str(ROOT / 'artifacts/depth-comparison'),
                        near='1', far='20', disparity='96', roi='', reference='')
        self.values = {k: tk.StringVar(value=v) for k, v in defaults.items()}
        self.controls = []
        for row, (key, label) in enumerate([('calibration', 'Каталог калибровки'), ('a', 'Кадр A'), ('b', 'Кадр B'), ('c', 'Кадр C'), ('output', 'Каталог результатов')], 2):
            ttk.Label(panel, text=label).grid(row=row, column=0, sticky='w', padx=(0, 12), pady=4)
            entry = ttk.Entry(panel, textvariable=self.values[key])
            entry.grid(row=row, column=1, sticky='ew')
            button = ttk.Button(panel, text='Выбрать…', command=lambda k=key: self.browse(k))
            button.grid(row=row, column=2, padx=(8, 0))
            self.controls.extend([entry, button])
        params = ttk.Frame(panel)
        params.grid(row=7, column=0, columnspan=3, sticky='ew', pady=12)
        for i, (key, label) in enumerate([('near', 'От, м'), ('far', 'До, м'), ('disparity', 'Макс. disparity'), ('roi', 'Область: X Y Ш В'), ('reference', 'Эталон Z, м')]):
            ttk.Label(params, text=label).grid(row=0, column=i, sticky='w', padx=5)
            entry = ttk.Entry(params, textvariable=self.values[key], width=22 if key == 'roi' else 12)
            entry.grid(row=1, column=i, padx=5, sticky='ew')
            self.controls.append(entry)
        ttk.Label(panel, text='Область и эталон необязательны. Эталон — глубина плоской мишени, параллельной плоскости изображения A.').grid(row=8, column=0, columnspan=3, sticky='w')
        buttons = ttk.Frame(panel)
        buttons.grid(row=9, column=0, columnspan=3, sticky='ew', pady=12)
        self.run_button = ttk.Button(buttons, text='Сравнить', command=self.start)
        self.run_button.pack(side='left')
        demo = ttk.Button(buttons, text='Загрузить демо', command=self.demo)
        demo.pack(side='left', padx=8)
        self.controls.extend([self.run_button, demo])
        self.open_button = ttk.Button(buttons, text='Открыть HTML-отчёт', command=self.open_report, state='disabled')
        self.open_button.pack(side='left')
        self.status = tk.StringVar(value='Выберите данные или загрузите синтетический пример.')
        ttk.Label(panel, textvariable=self.status).grid(row=10, column=0, columnspan=3, sticky='w')
        self.progress = ttk.Progressbar(panel, mode='indeterminate')
        self.progress.grid(row=11, column=0, columnspan=3, sticky='ew', pady=6)
        self.table = ttk.Treeview(panel, columns=('mode', 'coverage', 'depth', 'error', 'time'), show='headings', height=5)
        for key, title in zip(self.table['columns'], ['Режим', 'Заполнение, %', 'Медиана Z, м', 'RMSE, м', 'Время, мс']):
            self.table.heading(key, text=title)
            self.table.column(key, width=130)
        self.table.grid(row=12, column=0, columnspan=3, sticky='ew', pady=8)
        self.table.bind('<<TreeviewSelect>>', self.preview)
        self.picture = ttk.Label(panel, text='После расчёта выберите строку, чтобы посмотреть карту глубины.', anchor='center')
        self.picture.grid(row=13, column=0, columnspan=3, sticky='nsew')
        panel.rowconfigure(13, weight=1)
        self.log = tk.Text(panel, height=6, state='disabled', wrap='word')
        self.log.grid(row=14, column=0, columnspan=3, sticky='ew', pady=(10, 0))
        window.after(100, self.poll)

    def browse(self, key):
        path = filedialog.askdirectory(parent=self.window) if key in ('calibration', 'output') else filedialog.askopenfilename(parent=self.window, filetypes=[('Изображения', '*.png *.jpg *.jpeg *.bmp *.tif *.tiff')])
        if path:
            self.values[key].set(path)

    def demo(self):
        root = ROOT / 'artifacts/depth-comparison-demo'
        if not (root / 'calibration-draft.json').exists():
            messagebox.showinfo('Демо', 'Демонстрационный набор не найден. Выберите свои кадры и калибровку.')
            return
        self.values['calibration'].set(str(root))
        for camera in 'abc':
            self.values[camera].set(str(root / f'{camera.upper()}.png'))
        for key, value in dict(near='1', far='20', disparity='24', roi='30 8 70 30', reference='5').items():
            self.values[key].set(value)
        self.status.set('Синтетическая плоскость, Z = 5 м. Нажмите «Сравнить».')

    def start(self):
        try:
            args, output = command({k: v.get() for k, v in self.values.items()})
        except (ValueError, OSError) as error:
            messagebox.showerror('Проверьте параметры', str(error))
            return
        self.running = True
        self.report = None
        self.open_button.configure(state='disabled')
        self.table.delete(*self.table.get_children())
        self.picture.configure(image='', text='Выполняется расчёт…')
        self.log.configure(state='normal')
        self.log.delete('1.0', 'end')
        self.log.configure(state='disabled')
        for control in self.controls:
            control.configure(state='disabled')
        self.status.set('Сборка и расчёт пяти режимов. На больших кадрах это может занять несколько минут.')
        self.progress.start()
        threading.Thread(target=self.work, args=(args, output), daemon=True).start()

    def work(self, args, output):
        try:
            with subprocess.Popen(args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  text=True, encoding='utf-8', errors='replace',
                                  creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)) as process:
                for line in process.stdout:
                    self.events.put(('log', line))
                if process.wait() != 0:
                    raise RuntimeError('Расчёт завершился с ошибкой. Подробности — в журнале ниже.')
            report = json.loads((output / 'report.json').read_text(encoding='utf-8'))
            self.events.put(('done', (output, report)))
        except Exception as error:
            self.events.put(('error', str(error)))

    def poll(self):
        try:
            while True:
                kind, data = self.events.get_nowait()
                if kind == 'log':
                    self.log.configure(state='normal')
                    self.log.insert('end', data)
                    self.log.see('end')
                    self.log.configure(state='disabled')
                    continue
                self.running = False
                self.progress.stop()
                for control in self.controls:
                    control.configure(state='normal')
                if kind == 'error':
                    self.status.set(data)
                    self.picture.configure(image='', text='Результат не получен.')
                else:
                    self.report, report = data
                    for run in report['runs']:
                        m = run['metrics']
                        fmt = lambda value: '—' if value is None else f'{value:.3f}'
                        self.table.insert('', 'end', iid=run['mode'], values=(run['mode'].replace('_', '+'), fmt(m['coveragePercent']), fmt(m['medianDepthMetres']), fmt(m.get('rmseMetres')), fmt(run['elapsedMilliseconds'])))
                    self.open_button.configure(state='normal')
                    self.status.set(f'Готово. Добавлено BC в области анализа: {report["bcContribution"]["addedValidPixels"]} пикселей. Цветовая шкала — в HTML-отчёте.')
                    self.table.selection_set('AB_AC_BC')
        except queue.Empty:
            pass
        self.window.after(100, self.poll)

    def preview(self, _event=None):
        selection = self.table.selection()
        if self.report and selection:
            picture = tk.PhotoImage(file=str(self.report / f'{selection[0]}.png'))
            ratio = max(1, math.ceil(picture.width() / 950), math.ceil(picture.height() / 220))
            self.preview_image = picture.subsample(ratio)
            self.picture.configure(image=self.preview_image, text='')

    def open_report(self):
        if self.report:
            webbrowser.open((self.report / 'report.html').as_uri())

    def close(self):
        if self.running:
            messagebox.showinfo('Выполняется расчёт', 'Дождитесь завершения расчёта перед закрытием окна.')
        else:
            self.window.destroy()


if __name__ == '__main__':
    window = tk.Tk()
    App(window)
    window.mainloop()
