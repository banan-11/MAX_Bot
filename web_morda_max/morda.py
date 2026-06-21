from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse
from pydantic import BaseModel
from typing import List, Optional
from datetime import datetime
import asyncpg
import json
from db_config_private import DB_CONFIG


app = FastAPI(docs_url=None, redoc_url=None)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


class QuestionResponse(BaseModel):
    id: int
    user_id: int
    chat_id: int
    question_text: str
    status: str
    answer_text: Optional[str] = None
    created_at: datetime
    is_blocked: bool = False


class AnswerRequest(BaseModel):
    question_id: int
    answer_text: str
    answered_by: str = "Оператор"


class BlockUserRequest(BaseModel):
    user_id: int
    is_blocked: bool


# --------- МОДЕЛИ ДЛЯ FAQ ---------

class FaqItem(BaseModel):
    id: int
    question: str
    answer: str
    category: Optional[str] = None
    display_order: int


class FaqCreateRequest(BaseModel):
    question: str
    answer: str
    category: Optional[str] = None


async def get_db_connection():
    return await asyncpg.connect(**DB_CONFIG)


# ---------- API (вопросы пользователей) ----------

@app.get("/questions", response_model=List[QuestionResponse])
async def get_questions():
    conn = await get_db_connection()
    try:
        rows = await conn.fetch(
            """
            SELECT id,
                   user_id,
                   chat_id,
                   question_text,
                   status,
                   answer_text,
                   created_at,
                   COALESCE(is_blocked, false) AS is_blocked
            FROM user_questions
            ORDER BY created_at DESC
            """
        )
        return [dict(r) for r in rows]
    finally:
        await conn.close()


@app.post("/questions/answer")
async def answer_question(answer: AnswerRequest):
    conn = await get_db_connection()
    try:
        result = await conn.execute(
            """
            UPDATE user_questions
            SET answer_text = $1,
                status      = 'answered',
                answered_by = $2,
                answered_at = NOW()
            WHERE id = $3
            """,
            answer.answer_text,
            answer.answered_by,
            answer.question_id,
        )
        if not result.endswith("1"):
            raise HTTPException(status_code=404, detail="Вопрос не найден")
        return {"status": "ok"}
    finally:
        await conn.close()


@app.post("/questions/block_user")
async def block_user(req: BlockUserRequest):
    conn = await get_db_connection()
    try:
        async with conn.transaction():
            await conn.execute(
                "UPDATE user_questions SET is_blocked = $1 WHERE user_id = $2",
                req.is_blocked,
                req.user_id,
            )
            if req.is_blocked:
                await conn.execute(
                    """
                    INSERT INTO blocked_users (user_id)
                    VALUES ($1)
                    ON CONFLICT (user_id) DO NOTHING
                    """,
                    req.user_id,
                )
            else:
                await conn.execute(
                    "DELETE FROM blocked_users WHERE user_id = $1",
                    req.user_id,
                )
        return {"status": "ok"}
    finally:
        await conn.close()


# ---------- API для FAQ ----------

@app.get("/faq", response_model=List[FaqItem])
async def get_faq_items():
    conn = await get_db_connection()
    try:
        rows = await conn.fetch(
            """
            SELECT id,
                   question,
                   answer,
                   COALESCE(category, '') AS category,
                   COALESCE(display_order, 0) AS display_order
            FROM faq_items
            ORDER BY display_order ASC, id ASC
            """
        )
        return [dict(r) for r in rows]
    finally:
        await conn.close()


@app.post("/faq", response_model=FaqItem)
async def add_faq_item(req: FaqCreateRequest):
    conn = await get_db_connection()
    try:
        # берём максимальный display_order и добавляем 1
        row = await conn.fetchrow("SELECT COALESCE(MAX(display_order), 0) AS max_order FROM faq_items")
        max_order = row["max_order"] if row else 0
        new_order = max_order + 1

        inserted = await conn.fetchrow(
            """
            INSERT INTO faq_items (question, answer, category, display_order)
            VALUES ($1, $2, $3, $4)
            RETURNING id, question, answer, category, display_order
            """,
            req.question,
            req.answer,
            req.category,
            new_order,
        )
        return FaqItem(**dict(inserted))
    finally:
        await conn.close()


@app.delete("/faq/{faq_id}")
async def delete_faq_item(faq_id: int):
    conn = await get_db_connection()
    try:
        result = await conn.execute(
            "DELETE FROM faq_items WHERE id = $1",
            faq_id,
        )
        if not result.endswith("1"):
            raise HTTPException(status_code=404, detail="FAQ не найден")
        return {"status": "ok"}
    finally:
        await conn.close()


# ---------- ПАНЕЛЬ ОПЕРАТОРА ----------

@app.get("/panel", response_class=HTMLResponse, include_in_schema=False)
async def operator_panel():
    conn = await get_db_connection()
    try:
        rows = await conn.fetch(
            """
            SELECT id,
                   user_id,
                   chat_id,
                   question_text,
                   status,
                   answer_text,
                   created_at,
                   COALESCE(is_blocked, false) AS is_blocked
            FROM user_questions
            ORDER BY created_at DESC
            """
        )
        questions = []
        for r in rows:
            d = dict(r)
            if isinstance(d["created_at"], datetime):
                d["created_at"] = d["created_at"].isoformat()
            questions.append(d)
    finally:
        await conn.close()

    html = """
    <!DOCTYPE html>
    <html lang="ru">
    <head>
        <meta charset="UTF-8">
        <title>Панель поддержки КГТС</title>
        <style>
            * { box-sizing: border-box; }
            body {
                margin: 0;
                font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                background: #f5f5f5;
                color: #222;
                display: flex;
                height: 100vh;
            }
            body.dark {
                background: #121212;
                color: #eee;
            }
            .sidebar {
                width: 35%;
                max-width: 500px;
                background: #ffffff;
                border-right: 1px solid #ddd;
                overflow-y: auto;
            }
            body.dark .sidebar {
                background: #1e1e1e;
                border-right-color: #333;
            }
            .content {
                flex: 1;
                display: flex;
                flex-direction: column;
                padding: 16px;
            }
            body.dark .content {
                background: #121212;
            }
            .header {
                padding: 12px 16px;
                border-bottom: 1px solid #eee;
                font-weight: 600;
                background: #fafafa;
                position: sticky;
                top: 0;
                z-index: 1;
                display: flex;
                justify-content: space-between;
                align-items: center;
                gap: 8px;
            }
            body.dark .header {
                background: #1e1e1e;
                border-bottom-color: #333;
            }
            .header-controls {
                display: flex;
                gap: 8px;
                align-items: center;
                font-size: 12px;
            }
            .question-item {
                padding: 10px 16px;
                border-bottom: 1px solid #f0f0f0;
                cursor: pointer;
            }
            .question-item:hover { background: #f0f7ff; }
            body.dark .question-item:hover { background: #2a2a2a; }
            .question-item.selected { background: #e3f2fd; }
            body.dark .question-item.selected { background: #304050; }

            .question-status {
                display: inline-block;
                padding: 2px 6px;
                border-radius: 4px;
                font-size: 11px;
                text-transform: uppercase;
                margin-right: 6px;
            }
            .status-new { background: #e3f2fd; color: #1565c0; }
            .status-answered { background: #e8f5e9; color: #2e7d32; }
            .status-sent { background: #f3e5f5; color: #6a1b9a; }

            body.dark .status-new { background: #1e3a5f; color: #90caf9; }
            body.dark .status-answered { background: #1b5e20; color: #a5d6a7; }
            body.dark .status-sent { background: #4a148c; color: #e1bee7; }

            .question-text {
                font-size: 14px;
                margin-top: 4px;
                white-space: nowrap;
                overflow: hidden;
                text-overflow: ellipsis;
            }
            .question-meta {
                font-size: 11px;
                color: #777;
                margin-top: 4px;
            }
            body.dark .question-meta { color: #aaa; }

            .content-header {
                font-size: 18px;
                font-weight: 600;
                margin-bottom: 8px;
                display: flex;
                justify-content: space-between;
                align-items: center;
                gap: 12px;
            }
            .tabs {
                display: inline-flex;
                gap: 4px;
                border-radius: 4px;
                background: #e0e0e0;
                padding: 2px;
            }
            .tab-btn {
                border: none;
                padding: 4px 8px;
                font-size: 12px;
                cursor: pointer;
                border-radius: 3px;
                background: transparent;
            }
            .tab-btn.active {
                background: #ffffff;
            }
            body.dark .tabs { background: #333; }
            body.dark .tab-btn.active { background: #1e1e1e; }

            .content-subheader {
                font-size: 13px;
                color: #666;
                margin-bottom: 12px;
            }
            body.dark .content-subheader { color: #aaa; }

            .field-label {
                font-size: 12px;
                font-weight: 600;
                margin-top: 8px;
                margin-bottom: 4px;
            }
            textarea, input[type="text"] {
                width: 100%;
                padding: 8px;
                border-radius: 4px;
                border: 1px solid #ccc;
                font-size: 14px;
                background: #fff;
                color: #222;
            }
            textarea { min-height: 110px; resize: vertical; }
            body.dark textarea,
            body.dark input[type="text"] {
                background: #1e1e1e;
                color: #eee;
                border-color: #444;
            }

            .btn-row {
                margin-top: 12px;
                display: flex;
                gap: 8px;
                flex-wrap: wrap;
            }
            .btn {
                padding: 8px 14px;
                border-radius: 4px;
                border: none;
                cursor: pointer;
                font-size: 13px;
                font-weight: 600;
            }
            .btn-primary { background: #1976d2; color: #fff; }
            .btn-secondary { background: #eeeeee; color: #333; }
            .btn-danger { background: #c62828; color: #fff; }

            body.dark .btn-primary { background: #2196f3; }
            body.dark .btn-secondary { background: #333; color: #eee; }
            body.dark .btn-danger { background: #e53935; }

            .question-full-text {
                padding: 8px;
                background: #fff;
                border-radius: 4px;
                border: 1px solid #ddd;
                margin-bottom: 8px;
                white-space: pre-wrap;
                font-size: 14px;
            }
            body.dark .question-full-text {
                background: #1e1e1e;
                border-color: #444;
                color: #eee;
            }
            .question-meta-big {
                font-size: 12px;
                color: #666;
                margin-bottom: 10px;
            }
            body.dark .question-meta-big { color: #aaa; }

            .no-selection {
                color: #888;
                font-size: 14px;
                margin-top: 20px;
            }
            body.dark .no-selection { color: #aaa; }

            select {
                padding: 4px 8px;
                border-radius: 4px;
                border: 1px solid #ccc;
                font-size: 12px;
            }
            body.dark select {
                background: #1e1e1e;
                color: #eee;
                border-color: #444;
            }

            /* FAQ список */
            .faq-item {
                padding: 8px;
                border: 1px solid #ddd;
                border-radius: 4px;
                margin-bottom: 8px;
                background: #fff;
            }
            body.dark .faq-item {
                background: #1e1e1e;
                border-color: #444;
            }
            .faq-question {
                font-weight: 600;
                margin-bottom: 4px;
            }
            .faq-meta {
                font-size: 11px;
                color: #777;
                margin-top: 4px;
            }
            body.dark .faq-meta { color: #aaa; }
        </style>
    </head>
    <body>
        <div class="sidebar">
            <div class="header">
                <span>Вопросы пользователей</span>
                <div class="header-controls">
                    <select id="filterSelect" onchange="changeFilter()">
                        <option value="all">Все сообщения</option>
                        <option value="blocked">Недоброжелательные</option>
                    </select>
                    <button class="btn btn-secondary" onclick="toggleTheme()">Тёмная тема</button>
                </div>
            </div>
            <div id="questionList"></div>
        </div>
        <div class="content">
            <div class="content-header">
                <span>Поддержка КГТС</span>
                <div class="tabs">
                    <button class="tab-btn active" id="tab-questions" onclick="showTab('questions')">Вопросы</button>
                    <button class="tab-btn" id="tab-faq" onclick="showTab('faq')">FAQ</button>
                </div>
            </div>
            <div class="content-subheader" id="subheader">
                Выберите вопрос слева, напишите ответ и при необходимости добавьте его в FAQ.
            </div>
            <div id="details">
                <div class="no-selection">
                    Вопрос не выбран. Нажмите на строку слева, чтобы посмотреть детали.
                </div>
            </div>
            <div id="faqPanel" style="display:none;">
                <div class="field-label">Существующие записи FAQ</div>
                <div id="faqList" style="margin-bottom:12px;"></div>

                <div class="field-label">Добавить FAQ вручную</div>
                <div class="field-label">Вопрос</div>
                <textarea id="newFaqQuestion"></textarea>
                <div class="field-label">Ответ</div>
                <textarea id="newFaqAnswer"></textarea>
                <div class="field-label">Категория (опционально)</div>
                <input type="text" id="newFaqCategory" placeholder="Например: Общее">
                <div class="btn-row">
                    <button class="btn btn-primary" onclick="createFaqManual()">Добавить FAQ</button>
                    <button class="btn btn-secondary" onclick="reloadFaq()">Обновить список FAQ</button>
                </div>
            </div>
        </div>

        <script>
            let questions = %QUESTIONS%;
            let selectedId = null;
            let filterMode = 'all';
            let faqItems = [];

            (function initTheme() {
                const saved = localStorage.getItem('kgtc_theme');
                if (saved === 'dark') document.body.classList.add('dark');
            })();

            function toggleTheme() {
                document.body.classList.toggle('dark');
                localStorage.setItem(
                    'kgtc_theme',
                    document.body.classList.contains('dark') ? 'dark' : 'light'
                );
            }

            function showTab(tab) {
                const qTab = document.getElementById('tab-questions');
                const faqTab = document.getElementById('tab-faq');
                const details = document.getElementById('details');
                const faqPanel = document.getElementById('faqPanel');
                const subheader = document.getElementById('subheader');

                if (tab === 'questions') {
                    qTab.classList.add('active');
                    faqTab.classList.remove('active');
                    details.style.display = '';
                    faqPanel.style.display = 'none';
                    subheader.textContent = 'Выберите вопрос слева, напишите ответ и при необходимости добавьте его в FAQ.';
                } else {
                    qTab.classList.remove('active');
                    faqTab.classList.add('active');
                    details.style.display = 'none';
                    faqPanel.style.display = '';
                    subheader.textContent = 'Просмотр и управление записями FAQ.';
                    reloadFaq();
                }
            }

            function statusClass(status) {
                if (status === 'new') return 'status-new';
                if (status === 'answered') return 'status-answered';
                if (status === 'sent') return 'status-sent';
                return '';
            }

            function getFilteredQuestions() {
                if (filterMode === 'blocked') {
                    return questions.filter(q => q.is_blocked);
                }
                return questions.filter(q => !q.is_blocked);
            }

            function renderList() {
                const list = document.getElementById('questionList');
                list.innerHTML = '';
                const items = getFilteredQuestions();
                if (!items.length) {
                    list.innerHTML = '<div class="question-item">Нет вопросов для выбранного фильтра.</div>';
                    return;
                }
                items.forEach(q => {
                    const div = document.createElement('div');
                    div.className = 'question-item' + (q.id === selectedId ? ' selected' : '');
                    div.onclick = () => selectQuestion(q.id);

                    const statusDiv = document.createElement('div');
                    statusDiv.innerHTML =
                        '<span class="question-status ' + statusClass(q.status) + '">' +
                        q.status.toUpperCase() + '</span>' +
                        'ID ' + q.id + (q.is_blocked ? ' • недоброжелательный' : '');

                    const textDiv = document.createElement('div');
                    textDiv.className = 'question-text';
                    textDiv.textContent = q.question_text;

                    const metaDiv = document.createElement('div');
                    metaDiv.className = 'question-meta';
                    metaDiv.textContent = 'user_id: ' + q.user_id + ', chat_id: ' + q.chat_id;

                    div.appendChild(statusDiv);
                    div.appendChild(textDiv);
                    div.appendChild(metaDiv);
                    list.appendChild(div);
                });
            }

            function changeFilter() {
                filterMode = document.getElementById('filterSelect').value;
                selectedId = null;
                document.getElementById('details').innerHTML =
                    '<div class="no-selection">Вопрос не выбран. Нажмите на строку слева, чтобы посмотреть детали.</div>';
                renderList();
            }

            function selectQuestion(id) {
                selectedId = id;
                renderList();

                const q = questions.find(x => x.id === id);
                if (!q) return;

                const details = document.getElementById('details');
                details.innerHTML = '';

                const container = document.createElement('div');

                const meta = document.createElement('div');
                meta.className = 'question-meta-big';
                meta.textContent =
                    'ID ' + q.id +
                    ' | user_id: ' + q.user_id +
                    ' | chat_id: ' + q.chat_id +
                    ' | статус: ' + q.status +
                    (q.is_blocked ? ' | помечен как недоброжелательный' : '');

                const qText = document.createElement('div');
                qText.className = 'question-full-text';
                qText.textContent = q.question_text;

                const labelAnswer = document.createElement('div');
                labelAnswer.className = 'field-label';
                labelAnswer.textContent = 'Ответ пользователю';

                const textarea = document.createElement('textarea');
                textarea.id = 'answerText';
                textarea.value = q.answer_text || '';

                const labelAnsweredBy = document.createElement('div');
                labelAnsweredBy.className = 'field-label';
                labelAnsweredBy.textContent = 'Подпись (кто отвечает)';

                const answeredByInput = document.createElement('input');
                answeredByInput.type = 'text';
                answeredByInput.id = 'answeredBy';
                answeredByInput.value = 'Оператор';

                const btnRow = document.createElement('div');
                btnRow.className = 'btn-row';

                const btnSend = document.createElement('button');
                btnSend.className = 'btn btn-primary';
                btnSend.textContent = 'Сохранить ответ';
                btnSend.onclick = () => sendAnswer(q.id);

                const btnToFaq = document.createElement('button');
                btnToFaq.className = 'btn btn-secondary';
                btnToFaq.textContent = 'Добавить в FAQ';
                btnToFaq.onclick = () => addToFaqFromQuestion(q.id);

                const btnBlock = document.createElement('button');
                btnBlock.className = 'btn btn-danger';
                btnBlock.textContent = q.is_blocked
                    ? 'Снять пометку недоброжелательного'
                    : 'Пометить пользователя как недоброжелательного';
                btnBlock.onclick = () => toggleBlockUser(q.user_id, !q.is_blocked);

                btnRow.appendChild(btnSend);
                btnRow.appendChild(btnToFaq);
                btnRow.appendChild(btnBlock);

                container.appendChild(meta);
                container.appendChild(qText);
                container.appendChild(labelAnswer);
                container.appendChild(textarea);
                container.appendChild(labelAnsweredBy);
                container.appendChild(answeredByInput);
                container.appendChild(btnRow);

                details.appendChild(container);
            }

            async function sendAnswer(questionId) {
                const q = questions.find(x => x.id === questionId);
                if (!q) return;

                const answerText = document.getElementById('answerText').value.trim();
                const answeredBy = document.getElementById('answeredBy').value.trim() || 'Оператор';
                if (!answerText) {
                    alert('Введите текст ответа');
                    return;
                }
                try {
                    const resp = await fetch('/questions/answer', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            question_id: questionId,
                            answer_text: answerText,
                            answered_by: answeredBy
                        })
                    });
                    if (!resp.ok) {
                        const data = await resp.json();
                        alert('Ошибка: ' + (data.detail || resp.status));
                        return;
                    }
                    q.answer_text = answerText;
                    q.status = 'answered';
                    renderList();
                    selectQuestion(questionId);
                } catch (e) {
                    alert('Сетевая ошибка: ' + e);
                }
            }

            async function toggleBlockUser(userId, isBlocked) {
                try {
                    const resp = await fetch('/questions/block_user', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ user_id: userId, is_blocked: isBlocked })
                    });
                    if (!resp.ok) {
                        const data = await resp.json();
                        alert('Ошибка: ' + (data.detail || resp.status));
                        return;
                    }
                    questions.forEach(q => {
                        if (q.user_id === userId) q.is_blocked = isBlocked;
                    });
                    if (filterMode === 'blocked' && !isBlocked) {
                        selectedId = null;
                        document.getElementById('details').innerHTML =
                            '<div class="no-selection">Вопрос не выбран. Нажмите на строку слева, чтобы посмотреть детали.</div>';
                    }
                    renderList();
                } catch (e) {
                    alert('Сетевая ошибка: ' + e);
                }
            }

            async function fetchQuestions() {
                try {
                    const resp = await fetch('/questions');
                    if (!resp.ok) return;
                    const data = await resp.json();

                    const listElem = document.getElementById('questionList');
                    const prevScrollTop = listElem ? listElem.scrollTop : 0;
                    const currentSelectedId = selectedId;

                    questions = data;
                    renderList();

                    if (listElem) listElem.scrollTop = prevScrollTop;

                    if (currentSelectedId) {
                        const exists = questions.find(q => q.id === currentSelectedId);
                        if (!exists) {
                            selectedId = null;
                            document.getElementById('details').innerHTML =
                                '<div class="no-selection">Вопрос не выбран. Нажмите на строку слева, чтобы посмотреть детали.</div>';
                        }
                    }
                } catch (e) {
                    console.warn('Ошибка автообновления /questions:', e);
                }
            }

            // ---------- FAQ JS ----------

            async function reloadFaq() {
                try {
                    const resp = await fetch('/faq');
                    if (!resp.ok) {
                        console.warn('Ошибка загрузки FAQ:', resp.status);
                        return;
                    }
                    faqItems = await resp.json();
                    renderFaqList();
                } catch (e) {
                    console.warn('Ошибка /faq:', e);
                }
            }

            function renderFaqList() {
                const list = document.getElementById('faqList');
                list.innerHTML = '';
                if (!faqItems.length) {
                    list.innerHTML = '<div class="no-selection">FAQ пока пуст.</div>';
                    return;
                }
                faqItems.forEach(item => {
                    const div = document.createElement('div');
                    div.className = 'faq-item';

                    const qDiv = document.createElement('div');
                    qDiv.className = 'faq-question';
                    qDiv.textContent = item.question;

                    const aDiv = document.createElement('div');
                    aDiv.textContent = item.answer;

                    const meta = document.createElement('div');
                    meta.className = 'faq-meta';
                    meta.textContent = 'ID ' + item.id +
                        (item.category ? ' | Категория: ' + item.category : '') +
                        ' | Порядок: ' + item.display_order;

                    const btnRow = document.createElement('div');
                    btnRow.className = 'btn-row';

                    const btnDel = document.createElement('button');
                    btnDel.className = 'btn btn-danger';
                    btnDel.textContent = 'Удалить';
                    btnDel.onclick = () => deleteFaq(item.id);

                    btnRow.appendChild(btnDel);

                    div.appendChild(qDiv);
                    div.appendChild(aDiv);
                    div.appendChild(meta);
                    div.appendChild(btnRow);

                    list.appendChild(div);
                });
            }

            async function deleteFaq(id) {
                if (!confirm('Удалить эту запись FAQ?')) return;
                try {
                    const resp = await fetch('/faq/' + id, { method: 'DELETE' });
                    if (!resp.ok) {
                        const data = await resp.json();
                        alert('Ошибка удаления FAQ: ' + (data.detail || resp.status));
                        return;
                    }
                    faqItems = faqItems.filter(f => f.id !== id);
                    renderFaqList();
                } catch (e) {
                    alert('Сетевая ошибка: ' + e);
                }
            }

            async function addToFaqFromQuestion(questionId) {
                const q = questions.find(x => x.id === questionId);
                if (!q) return;

                const answerText = document.getElementById('answerText').value.trim();
                if (!answerText) {
                    alert('Сначала введите и сохраните ответ, потом добавляйте в FAQ.');
                    return;
                }

                if (!confirm('Добавить этот вопрос и ответ в FAQ?')) return;

                try {
                    const resp = await fetch('/faq', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            question: q.question_text,
                            answer: answerText,
                            category: 'Общее'
                        })
                    });
                    if (!resp.ok) {
                        const data = await resp.json();
                        alert('Ошибка добавления в FAQ: ' + (data.detail || resp.status));
                        return;
                    }
                    alert('Добавлено в FAQ');
                } catch (e) {
                    alert('Сетевая ошибка: ' + e);
                }
            }

            async function createFaqManual() {
                const qText = document.getElementById('newFaqQuestion').value.trim();
                const aText = document.getElementById('newFaqAnswer').value.trim();
                const cat = document.getElementById('newFaqCategory').value.trim() || null;

                if (!qText || !aText) {
                    alert('Заполните вопрос и ответ');
                    return;
                }

                try {
                    const resp = await fetch('/faq', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            question: qText,
                            answer: aText,
                            category: cat
                        })
                    });
                    if (!resp.ok) {
                        const data = await resp.json();
                        alert('Ошибка добавления FAQ: ' + (data.detail || resp.status));
                        return;
                    }
                    document.getElementById('newFaqQuestion').value = '';
                    document.getElementById('newFaqAnswer').value = '';
                    document.getElementById('newFaqCategory').value = '';
                    await reloadFaq();
                } catch (e) {
                    alert('Сетевая ошибка: ' + e);
                }
            }

            renderList();
            setInterval(fetchQuestions, 5000);
        </script>
    </body>
    </html>
    """

    html = html.replace("%QUESTIONS%", json.dumps(questions, ensure_ascii=False))
    return HTMLResponse(html)