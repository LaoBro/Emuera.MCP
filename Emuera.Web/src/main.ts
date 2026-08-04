import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import './styles/fonts.css';
import './styles/float-btn.css';

const app = createApp(App);
app.use(createPinia());
app.mount('#app');
