import { createRouter, createWebHistory } from 'vue-router'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'all', component: () => import('../views/AllView.vue') },
    { path: '/new', name: 'new', component: () => import('../views/NewView.vue') },
    {
      path: '/instance/:token',
      name: 'instance',
      component: () => import('../views/InstanceView.vue'),
      props: true,
    },
    {
      path: '/console/:token',
      name: 'console',
      component: () => import('../views/ConsoleView.vue'),
      props: true,
    },
  ],
})
