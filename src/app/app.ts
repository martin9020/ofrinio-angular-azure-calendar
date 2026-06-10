import { NgStyle } from '@angular/common';
import { Component, computed, signal } from '@angular/core';

type AvailabilityStatus = 'free' | 'booked' | 'pending';

interface Photo {
  file: string;
  label: string;
}

interface CalendarDay {
  day: number | null;
  dateKey: string;
  status: AvailabilityStatus;
  isToday: boolean;
}

interface AvailabilityEntry {
  date: string;
  status: AvailabilityStatus;
}

declare global {
  interface Window {
    OFRINIO_API_BASE?: string;
  }
}

const MONTHS_BG = [
  'Януари',
  'Февруари',
  'Март',
  'Април',
  'Май',
  'Юни',
  'Юли',
  'Август',
  'Септември',
  'Октомври',
  'Ноември',
  'Декември',
];

const WEEKDAYS_BG = ['Пон', 'Вт', 'Ср', 'Чет', 'Пет', 'Съб', 'Нед'];

const FALLBACK_AVAILABILITY: AvailabilityEntry[] = [
  { date: '2026-06-20', status: 'pending' },
  { date: '2026-06-21', status: 'pending' },
  { date: '2026-07-05', status: 'booked' },
  { date: '2026-07-06', status: 'booked' },
  { date: '2026-07-07', status: 'booked' },
  { date: '2026-07-08', status: 'booked' },
  { date: '2026-08-10', status: 'booked' },
  { date: '2026-08-11', status: 'booked' },
  { date: '2026-08-12', status: 'booked' },
  { date: '2026-09-02', status: 'pending' },
];

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [NgStyle],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  readonly phone = '0888 124 195';
  readonly phoneHref = 'tel:+359888124195';
  readonly mapHref = 'https://maps.app.goo.gl/c3q7VnYSKXxaoDFJ7?g_st=ic';
  readonly weekdays = WEEKDAYS_BG;
  readonly selectedPhoto = signal<Photo | null>(null);
  readonly selectedDates = signal('');
  readonly apiBase = signal(window.OFRINIO_API_BASE?.replace(/\/$/, '') ?? '');
  readonly apiStatus = signal(this.apiBase().length
    ? 'Свързване към Azure API...'
    : 'Локален демо календар. Готово за свързване с Azure API.');
  readonly isAzureConnected = computed(() => this.apiBase().length > 0);
  readonly availability = signal(new Map<string, AvailabilityStatus>());
  readonly activeMonth = signal(this.initialMonth());
  readonly requestSent = signal(false);
  readonly availabilityTotals = computed(() => {
    const totals = { free: 0, booked: 0, pending: 0 };
    for (const day of this.calendarDays()) {
      if (day.day !== null) {
        totals[day.status] += 1;
      }
    }
    return totals;
  });

  readonly facts = [
    ['Разстояние', '50 м от плажа'],
    ['Капацитет', 'до 4 гости'],
    ['Площ', '45 кв.м.'],
    ['Престой', 'мин. 6 нощувки'],
  ];

  readonly photos: Photo[] = [
    { file: 'IMG_4420_Original.JPG', label: 'Тераса с маса и столове' },
    { file: 'IMG_4424_Original.JPG', label: 'Спалня' },
    { file: 'IMG_4458_Original.JPG', label: 'Всекидневна с ТВ' },
    { file: 'IMG_4460_Original.JPG', label: 'Кухненски бокс' },
    { file: 'IMG_4462_Original.JPG', label: 'Разтегателни дивани' },
    { file: 'IMG_4436_Original.JPG', label: 'Баня с душ' },
    { file: 'IMG_4463_Original.JPG', label: 'Изход към терасата' },
    { file: 'IMG_4413_Original.JPG', label: 'Подход към апартамента' },
  ];

  readonly amenities = [
    ['Плаж', '4 шезлонга и 2 чадъра за свободно плажуване.'],
    ['Кухня', 'Ел. печка, кафе машина, еър фрайър, хладилник и посуда.'],
    ['Комфорт', 'Климатик, ТВ с български канали, тераса и уютна дневна.'],
    ['Паркиране', 'Пред апартамента или на безплатен паркинг в съседство.'],
  ];

  readonly prices = [
    ['Май', '65€', 'спокоен старт на сезона'],
    ['Юни и Септември', '75€', 'най-добър баланс'],
    ['Юли и Август', '85€', 'активен сезон'],
  ];

  readonly currentMonthTitle = computed(() => {
    const value = this.activeMonth();
    return `${MONTHS_BG[value.getMonth()]} ${value.getFullYear()}`;
  });

  readonly calendarDays = computed(() => this.buildCalendarDays(this.activeMonth()));

  constructor() {
    this.seedAvailability(FALLBACK_AVAILABILITY);
    void this.loadAvailabilityFromApi();
  }

  photoUrl(photo: Photo): string {
    return `assets/photos/${photo.file}`;
  }

  heroStyle(): Record<string, string> {
    return {
      'background-image':
        'linear-gradient(90deg, rgba(10, 36, 43, 0.78), rgba(10, 36, 43, 0.30)), url("assets/hero-beach.jpg")',
    };
  }

  openPhoto(photo: Photo): void {
    this.selectedPhoto.set(photo);
  }

  closePhoto(): void {
    this.selectedPhoto.set(null);
  }

  previousMonth(): void {
    const value = this.activeMonth();
    this.activeMonth.set(new Date(value.getFullYear(), value.getMonth() - 1, 1));
  }

  nextMonth(): void {
    const value = this.activeMonth();
    this.activeMonth.set(new Date(value.getFullYear(), value.getMonth() + 1, 1));
  }

  onRequestSubmit(event: Event): void {
    event.preventDefault();
    const form = event.target as HTMLFormElement;
    const formData = new FormData(form);
    const name = (formData.get('name') as string).trim();
    const phone = (formData.get('phone') as string).trim();
    const requestedDates = (formData.get('dates') as string || this.selectedDates()).trim();

    if (!name || !phone || !requestedDates) {
      this.apiStatus.set('Моля, попълнете име, телефон и желани дати.');
      return;
    }

    const apiBase = this.apiBase();
    if (apiBase) {
      this.apiStatus.set('Изпращане на запитване...');
      fetch(`${apiBase}/api/booking-requests`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'application/json',
        },
        body: JSON.stringify({ name, phone, requestedDates, message: '' }),
      })
        .then((res) => {
          if (!res.ok) throw new Error();
          this.requestSent.set(true);
          this.apiStatus.set('Запитването е изпратено успешно.');
          form.reset();
          this.selectedDates.set('');
        })
        .catch(() => {
          this.apiStatus.set('Възникна грешка при изпращането.');
        });
    } else {
      this.requestSent.set(true);
      this.apiStatus.set('Демо режим: заявката ще се запази само локално.');
      form.reset();
      this.selectedDates.set('');
    }
  }

  statusLabel(status: AvailabilityStatus): string {
    if (status === 'booked') return 'Заето';
    if (status === 'pending') return 'Запитване';
    return 'Свободно';
  }

  selectDay(day: CalendarDay): void {
    if (day.day === null) {
      return;
    }

    this.selectedDates.set(day.dateKey);
    this.apiStatus.set(`Избрана дата: ${day.dateKey}. Попълнете желания период във формата.`);
  }

  private initialMonth(): Date {
    const today = new Date();
    return new Date(today.getFullYear(), today.getMonth(), 1);
  }

  private seedAvailability(entries: AvailabilityEntry[]): void {
    const map = new Map<string, AvailabilityStatus>();
    for (const entry of entries) {
      map.set(entry.date, entry.status);
    }
    this.availability.set(map);
  }

  private async loadAvailabilityFromApi(): Promise<void> {
    const apiBase = window.OFRINIO_API_BASE?.replace(/\/$/, '');
    if (!apiBase) return;

    try {
      const response = await fetch(`${apiBase}/api/availability`, {
        headers: { Accept: 'application/json' },
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const rows = (await response.json()) as AvailabilityEntry[];
      this.seedAvailability(rows);
      this.apiStatus.set('Календарът е зареден от Azure API.');
    } catch {
      this.apiStatus.set('Azure API не е достъпно. Показва се локален демо календар.');
    }
  }

  private buildCalendarDays(monthDate: Date): CalendarDay[] {
    const year = monthDate.getFullYear();
    const month = monthDate.getMonth();
    const firstDay = (new Date(year, month, 1).getDay() + 6) % 7;
    const daysInMonth = new Date(year, month + 1, 0).getDate();
    const today = new Date();
    const output: CalendarDay[] = [];

    for (let index = 0; index < firstDay; index += 1) {
      output.push({ day: null, dateKey: `empty-${index}`, status: 'free', isToday: false });
    }

    for (let day = 1; day <= daysInMonth; day += 1) {
      const dateKey = `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
      output.push({
        day,
        dateKey,
        status: this.availability().get(dateKey) ?? 'free',
        isToday:
          today.getFullYear() === year && today.getMonth() === month && today.getDate() === day,
      });
    }

    return output;
  }
}
